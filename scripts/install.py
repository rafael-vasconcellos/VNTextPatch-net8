#!/usr/bin/env python3
"""Instala/atualiza o VNTextPatch usando apenas a biblioteca padrão do Python."""

from __future__ import annotations

import argparse
import ctypes
import hashlib
import http.client
import json
import os
import platform
import re
import shlex
import shutil
import socket
import stat
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import zipfile
from pathlib import Path, PurePosixPath
from typing import Any


REPOSITORY = "rafael-vasconcellos/VNTextPatch-net8"
API_BASE = f"https://api.github.com/repos/{REPOSITORY}/releases"
EXECUTABLE_BASENAME = "VNTextPatch"
LINK_NAME = "VNTextPatch"
USER_AGENT = "VNTextPatch-installer/1.0"
CHUNK_SIZE = 1024 * 1024
MAX_RETRIES = 3
REQUEST_TIMEOUT = 30
CHECKSUM_PATTERN = re.compile(r"(?i)(?<![0-9a-f])[0-9a-f]{64}(?![0-9a-f])")
VERSION_PATTERN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]*$")
WINDOWS_LAUNCHER_MARKER = "REM Managed by VNTextPatch installer"


class InstallerError(Exception):
    """Erro esperado que pode ser apresentado diretamente ao usuário."""


def fail(message: str) -> None:
    raise InstallerError(message)


def detect_platform() -> tuple[str, str]:
    """Retorna (RID do pacote, nome do executável) para o sistema atual."""
    system = platform.system().lower()
    machine = platform.machine().lower()

    if system == "windows":
        if machine in {"amd64", "x86_64", "x64"}:
            return "win-x64", f"{EXECUTABLE_BASENAME}.exe"
        fail(f"Arquitetura Windows não suportada: {platform.machine()}.")

    if system == "linux":
        if machine in {"x86_64", "amd64"}:
            return "linux-x64", EXECUTABLE_BASENAME
        fail(f"Arquitetura Linux não suportada: {platform.machine()}.")

    if system == "darwin":
        if machine == "x86_64":
            return "osx-x64", EXECUTABLE_BASENAME
        if machine in {"arm64", "aarch64"}:
            return "osx-arm64", EXECUTABLE_BASENAME
        fail(f"Arquitetura macOS não suportada: {platform.machine()}.")

    fail(f"Sistema operacional não suportado: {platform.system()}.")


def default_directories() -> tuple[Path, Path]:
    """Diretórios por usuário: não exigem privilégios administrativos."""
    home = Path.home()
    if os.name == "nt":
        local_app_data = os.environ.get("LOCALAPPDATA")
        base = Path(local_app_data) if local_app_data else home / "AppData" / "Local"
        root = base / "Programs" / "VNTextPatch"
        return root, root / "bin"

    return home / ".local" / "opt" / "VNTextPatch", home / ".local" / "bin"


def parse_arguments() -> argparse.Namespace:
    default_root, default_bin = default_directories()
    parser = argparse.ArgumentParser(
        description="Instala ou atualiza o VNTextPatch para a plataforma atual.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=(
            "Exemplos:\n"
            "  python install_vntextpatch.py\n"
            "  python install_vntextpatch.py --version v1.0.100\n"
            "  python install_vntextpatch.py --install-root ~/apps/VNTextPatch\n"
            "  python install_vntextpatch.py --bin-dir ~/.local/bin\n\n"
            "Variáveis de ambiente:\n"
            "  VNTP_INSTALL_ROOT  Sobrescreve o diretório das versões.\n"
            "  VNTP_BIN_DIR       Sobrescreve o diretório do comando."
        ),
    )
    parser.add_argument("-v", "--version", help="Tag específica (ex.: v1.0.100).")
    parser.add_argument(
        "-i", "--install-root", "--install-dir",
        help=f"Diretório das versões (padrão: {default_root}).",
    )
    parser.add_argument(
        "-b", "--bin-dir",
        help=f"Diretório do comando VNTextPatch (padrão: {default_bin}).",
    )
    parser.add_argument(
        "--no-path", action="store_true",
        help="Não altera o PATH persistente do usuário.",
    )
    parser.add_argument(
        "positional_install_root", nargs="?",
        help="Diretório de instalação (compatibilidade com o instalador anterior).",
    )
    args = parser.parse_args()

    if args.install_root and args.positional_install_root:
        parser.error("use --install-root ou o argumento posicional, não ambos")
    if args.version and not VERSION_PATTERN.fullmatch(args.version):
        parser.error("tag de versão inválida")

    root_value = (
        args.install_root
        or args.positional_install_root
        or os.environ.get("VNTP_INSTALL_ROOT")
        or str(default_root)
    )
    bin_value = args.bin_dir or os.environ.get("VNTP_BIN_DIR") or str(default_bin)
    args.install_root = Path(root_value).expanduser()
    args.bin_dir = Path(bin_value).expanduser()
    return args


def make_request(url: str, headers: dict[str, str] | None = None) -> urllib.request.Request:
    request_headers = {"User-Agent": USER_AGENT}
    if headers:
        request_headers.update(headers)
    return urllib.request.Request(url, headers=request_headers, method="GET")


def open_with_retries(
    request: urllib.request.Request,
    *,
    opener: urllib.request.OpenerDirector | None = None,
) -> Any:
    client = opener or urllib.request.build_opener()
    last_error: BaseException | None = None

    for attempt in range(MAX_RETRIES + 1):
        try:
            return client.open(request, timeout=REQUEST_TIMEOUT)
        except urllib.error.HTTPError as exc:
            last_error = exc
            retryable = exc.code in {408, 425, 429, 500, 502, 503, 504}
            if not retryable or attempt >= MAX_RETRIES:
                raise
            retry_after = exc.headers.get("Retry-After")
            try:
                delay = min(max(int(retry_after), 1), 15) if retry_after else attempt + 1
            except ValueError:
                delay = attempt + 1
            exc.close()
        except (urllib.error.URLError, TimeoutError, socket.timeout, ConnectionError) as exc:
            last_error = exc
            if attempt >= MAX_RETRIES:
                raise
            delay = attempt + 1

        time.sleep(delay)

    assert last_error is not None
    raise last_error


def get_release(requested_version: str | None) -> dict[str, Any]:
    url = f"{API_BASE}/tags/{urllib.parse.quote(requested_version, safe='')}" if requested_version else f"{API_BASE}/latest"
    request = make_request(
        url,
        {
            "Accept": "application/vnd.github+json",
            "X-GitHub-Api-Version": "2022-11-28",
        },
    )
    try:
        with open_with_retries(request) as response:
            final = urllib.parse.urlsplit(response.geturl())
            if final.scheme != "https" or final.hostname != "api.github.com":
                fail("A consulta da release foi redirecionada para um endereço não permitido.")
            payload = json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        if exc.code == 404:
            fail(f"A versão {requested_version!r} não foi encontrada no GitHub.")
        fail(f"Falha ao consultar o GitHub (HTTP {exc.code}).")
    except (urllib.error.URLError, TimeoutError, socket.timeout, json.JSONDecodeError, UnicodeDecodeError) as exc:
        fail(f"Não foi possível consultar os metadados da release: {exc}")

    if not isinstance(payload, dict):
        fail("A API do GitHub retornou metadados inválidos.")

    tag = payload.get("tag_name")
    if not isinstance(tag, str) or not VERSION_PATTERN.fullmatch(tag):
        fail("A resposta do GitHub não contém uma tag de versão válida.")
    if requested_version and tag != requested_version:
        fail(f"Foi solicitada a versão {requested_version}, mas o GitHub retornou {tag}.")
    return payload


def validate_asset_url(url: str, tag: str, asset_name: str) -> None:
    try:
        parsed = urllib.parse.urlsplit(url)
        port = parsed.port
    except ValueError:
        fail("URL do asset inválida.")

    expected_path = f"/{REPOSITORY}/releases/download/{tag}/{asset_name}"
    if (
        parsed.scheme != "https"
        or parsed.hostname != "github.com"
        or port not in (None, 443)
        or parsed.username is not None
        or parsed.password is not None
        or parsed.query
        or parsed.fragment
        or urllib.parse.unquote(parsed.path) != expected_path
    ):
        fail("URL do asset não corresponde ao endereço oficial esperado do GitHub.")


def validate_effective_url(url: str) -> None:
    try:
        parsed = urllib.parse.urlsplit(url)
        port = parsed.port
    except ValueError:
        fail("URL final do download inválida.")

    allowed_hosts = {
        "github.com",
        "release-assets.githubusercontent.com",
        "objects.githubusercontent.com",
    }
    if (
        parsed.scheme != "https"
        or parsed.hostname not in allowed_hosts
        or port not in (None, 443)
        or parsed.username is not None
        or parsed.password is not None
    ):
        fail("O download foi redirecionado para um host não permitido.")


class SafeRedirectHandler(urllib.request.HTTPRedirectHandler):
    max_redirections = 5
    max_repeats = 2

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        validate_effective_url(newurl)
        return super().redirect_request(req, fp, code, msg, headers, newurl)


DOWNLOAD_OPENER = urllib.request.build_opener(SafeRedirectHandler())


def find_assets(release: dict[str, Any], platform_id: str, tag: str) -> tuple[str, str | None, str | None]:
    asset_name = f"VNTextPatch-{platform_id}-{tag}.zip"
    assets = release.get("assets")
    if not isinstance(assets, list):
        fail("A release não contém uma lista válida de assets.")

    asset = next(
        (item for item in assets if isinstance(item, dict) and item.get("name") == asset_name),
        None,
    )
    if asset is None:
        available = sorted(
            item.get("name", "")
            for item in assets
            if isinstance(item, dict) and isinstance(item.get("name"), str)
        )
        available_text = ", ".join(available) if available else "nenhum asset"
        fail(
            f"A release {tag} não oferece o pacote {asset_name} para esta plataforma.\n"
            f"Assets publicados: {available_text}"
        )

    download_url = asset.get("browser_download_url")
    if not isinstance(download_url, str) or not download_url:
        fail(f"A release {tag} não possui URL de download para {asset_name}.")

    digest = asset.get("digest")
    if not isinstance(digest, str):
        digest = None
    checksum_name = asset_name + ".sha256"
    checksum_asset = next(
        (item for item in assets if isinstance(item, dict) and item.get("name") == checksum_name),
        None,
    )
    checksum_url = checksum_asset.get("browser_download_url") if isinstance(checksum_asset, dict) else None
    if not isinstance(checksum_url, str) or not checksum_url:
        checksum_url = None

    return download_url, digest, checksum_url


def download_file(url: str, destination: Path) -> tuple[str, str]:
    request = make_request(url, {"Accept": "application/octet-stream"})
    transfer_errors = (
        urllib.error.URLError,
        TimeoutError,
        socket.timeout,
        ConnectionError,
        http.client.HTTPException,
    )

    for transfer_attempt in range(MAX_RETRIES + 1):
        try:
            with open_with_retries(request, opener=DOWNLOAD_OPENER) as response:
                effective_url = response.geturl()
                validate_effective_url(effective_url)
                digest = hashlib.sha256()
                with destination.open("wb") as output:
                    while True:
                        chunk = response.read(CHUNK_SIZE)
                        if not chunk:
                            break
                        output.write(chunk)
                        digest.update(chunk)
                return digest.hexdigest(), effective_url
        except urllib.error.HTTPError as exc:
            destination.unlink(missing_ok=True)
            fail(f"Falha no download (HTTP {exc.code}).")
        except transfer_errors as exc:
            destination.unlink(missing_ok=True)
            if transfer_attempt >= MAX_RETRIES:
                fail(f"Falha no download após {MAX_RETRIES + 1} tentativas: {exc}")
            time.sleep(transfer_attempt + 1)
        except OSError as exc:
            destination.unlink(missing_ok=True)
            fail(f"Falha ao gravar o download em disco: {exc}")

    fail("O download não foi concluído.")


def read_expected_checksum(checksum_path: Path) -> str:
    try:
        content = checksum_path.read_text(encoding="utf-8", errors="strict")
    except (OSError, UnicodeDecodeError) as exc:
        fail(f"Não foi possível ler o arquivo de checksum: {exc}")
    matches = CHECKSUM_PATTERN.findall(content)
    if len(matches) != 1:
        fail("O arquivo de checksum deve conter exatamente um hash SHA-256.")
    return matches[0].lower()


def safe_zip_path(name: str, expected_root: str) -> PurePosixPath:
    if not name or "\\" in name or "\x00" in name or name.startswith("/"):
        fail(f"Caminho inseguro no ZIP: {name!r}")
    path = PurePosixPath(name)
    if ".." in path.parts or not path.parts or path.parts[0] != expected_root:
        fail(f"Entrada fora da pasta esperada no ZIP: {name!r}")
    if any(":" in part for part in path.parts):
        fail(f"Caminho incompatível no ZIP: {name!r}")
    return path


def validate_and_extract(archive_path: Path, extract_dir: Path, expected_root: str, executable_name: str) -> Path:
    root = PurePosixPath(expected_root)
    executable_member = root / executable_name
    found_executable = False
    seen_names: set[str] = set()

    try:
        with zipfile.ZipFile(archive_path, "r") as archive:
            members = archive.infolist()
            if not members:
                fail("O ZIP está vazio.")

            for info in members:
                name = info.filename
                path = safe_zip_path(name, expected_root)
                normalized_name = str(path)
                if normalized_name in seen_names:
                    fail(f"O ZIP contém uma entrada duplicada: {name!r}")
                seen_names.add(normalized_name)

                if info.flag_bits & 0x1:
                    fail(f"Entrada criptografada não é permitida no ZIP: {name!r}")

                mode = (info.external_attr >> 16) & 0xFFFF
                file_type = stat.S_IFMT(mode)
                if stat.S_ISLNK(mode):
                    fail(f"Links simbólicos não são permitidos no ZIP: {name!r}")
                if file_type not in (0, stat.S_IFREG, stat.S_IFDIR):
                    fail(f"Tipo especial não permitido no ZIP: {name!r}")
                if path == executable_member and not info.is_dir():
                    found_executable = True

            if not found_executable:
                fail(f"O ZIP não contém {expected_root}/{executable_name}.")

            bad_member = archive.testzip()
            if bad_member is not None:
                fail(f"CRC inválido no ZIP: {bad_member}")

            extract_dir.mkdir(parents=True, exist_ok=True)
            for info in members:
                relative = safe_zip_path(info.filename, expected_root)
                destination = extract_dir.joinpath(*relative.parts)
                destination_parent = destination.parent
                destination_parent.mkdir(parents=True, exist_ok=True)

                if info.is_dir():
                    destination.mkdir(parents=True, exist_ok=True)
                    continue

                with archive.open(info, "r") as source, destination.open("xb") as output:
                    shutil.copyfileobj(source, output, length=CHUNK_SIZE)

                mode = (info.external_attr >> 16) & 0o777
                if os.name != "nt":
                    destination.chmod(mode or 0o644)

    except zipfile.BadZipFile as exc:
        fail(f"O arquivo baixado não é um ZIP íntegro: {exc}")
    except (OSError, RuntimeError, NotImplementedError) as exc:
        fail(f"Não foi possível validar ou extrair o ZIP: {exc}")

    published_dir = extract_dir / expected_root
    executable_path = published_dir / executable_name
    if not published_dir.is_dir() or not executable_path.is_file() or executable_path.is_symlink():
        fail("O executável não foi encontrado ou é inválido após a extração.")
    return executable_path


def normalized_path(path: Path) -> str:
    try:
        value = str(path.expanduser().resolve(strict=False))
    except OSError:
        value = os.path.abspath(os.path.expanduser(str(path)))
    return os.path.normcase(value) if os.name == "nt" else value


def path_contains(directory: Path, path_value: str | None = None) -> bool:
    current = os.environ.get("PATH", "") if path_value is None else path_value
    target = normalized_path(directory)
    for item in current.split(os.pathsep):
        if not item:
            continue
        expanded = os.path.expandvars(item)
        if normalized_path(Path(expanded)) == target:
            return True
    return False


def check_launcher_destination(launcher: Path, install_root: Path, executable_name: str) -> None:
    if os.name == "nt":
        if launcher.exists() or launcher.is_symlink():
            try:
                content = launcher.read_text(encoding="utf-8", errors="replace")
            except OSError:
                fail(f"Já existe um arquivo em {launcher} e ele não pode ser verificado.")
            if not content.startswith("@echo off\r\n" + WINDOWS_LAUNCHER_MARKER):
                fail(f"{launcher} já existe e não parece pertencer a esta instalação. Não será sobrescrito.")
        return

    if launcher.is_symlink():
        target = launcher.resolve(strict=False)
        try:
            relative = target.relative_to(install_root.resolve(strict=False))
        except ValueError:
            fail(f"Já existe um link em {launcher} apontando para outra instalação: {target}")
        if (
            len(relative.parts) != 2
            or not relative.parts[0].startswith("VNTextPatch-")
            or relative.parts[1] != executable_name
        ):
            fail(f"Já existe um link em {launcher} apontando para outra instalação: {target}")
    elif launcher.exists():
        fail(f"{launcher} já existe e não é um link simbólico. Remova-o ou escolha outro --bin-dir.")


def create_windows_launcher(launcher: Path, executable_path: Path) -> None:
    # Duplicar % evita que diretórios contendo '%' sejam interpretados como variáveis CMD.
    target = str(executable_path.resolve()).replace("%", "%%")
    content = (
        "@echo off\r\n"
        f"{WINDOWS_LAUNCHER_MARKER}\r\n"
        f'"{target}" %*\r\n'
    )
    temporary = launcher.with_name(f".{launcher.name}.{os.getpid()}.tmp")
    try:
        with temporary.open("w", encoding="utf-8", newline="") as stream:
            stream.write(content)
        os.replace(temporary, launcher)
    finally:
        temporary.unlink(missing_ok=True)


def update_launcher(
    bin_dir: Path,
    install_root: Path,
    executable_path: Path,
    executable_name: str,
) -> Path:
    launcher_name = f"{LINK_NAME}.cmd" if os.name == "nt" else LINK_NAME
    launcher = bin_dir / launcher_name
    check_launcher_destination(launcher, install_root, executable_name)

    if os.name == "nt":
        create_windows_launcher(launcher, executable_path)
        return launcher

    temporary_dir = Path(tempfile.mkdtemp(prefix=f".{LINK_NAME}-link-", dir=bin_dir))
    temporary_link = temporary_dir / LINK_NAME
    try:
        temporary_link.symlink_to(executable_path.resolve())
        os.replace(temporary_link, launcher)
    finally:
        shutil.rmtree(temporary_dir, ignore_errors=True)
    return launcher


def fish_quote(value: str) -> str:
    return "'" + value.replace("\\", "\\\\").replace("'", "\\'") + "'"


def update_posix_shell_path(bin_dir: Path) -> str:
    shell = Path(os.environ.get("SHELL", "")).name.lower()
    home = Path.home()

    if shell == "fish":
        config_dir = home / ".config" / "fish"
        config_file = config_dir / "config.fish"
        line = f"fish_add_path --global {fish_quote(str(bin_dir))}"
    elif shell == "zsh":
        config_file = home / ".zshrc"
        line = f"export PATH={shlex.quote(str(bin_dir))}:\"$PATH\""
    elif shell == "bash":
        config_file = home / ".bashrc"
        line = f"export PATH={shlex.quote(str(bin_dir))}:\"$PATH\""
    else:
        config_file = home / ".profile"
        line = f"export PATH={shlex.quote(str(bin_dir))}:\"$PATH\""

    marker = "# Added by VNTextPatch installer"
    config_dir = config_file.parent
    config_dir.mkdir(parents=True, exist_ok=True)
    existing = config_file.read_text(encoding="utf-8") if config_file.exists() else ""
    if line in existing.splitlines():
        return f"O PATH já está configurado em {config_file}. Abra um novo terminal."

    addition = f"\n{marker}\n{line}\n"
    config_file.write_text(existing.rstrip() + addition, encoding="utf-8")
    return f"PATH configurado em {config_file}. Abra um novo terminal para aplicar a alteração."


def broadcast_windows_environment_change() -> None:
    try:
        user32 = ctypes.WinDLL("user32", use_last_error=True)
        send_message = user32.SendMessageTimeoutW
        send_message.argtypes = [
            ctypes.c_void_p, ctypes.c_uint, ctypes.c_size_t,
            ctypes.c_void_p, ctypes.c_uint, ctypes.c_uint, ctypes.c_void_p,
        ]
        send_message.restype = ctypes.c_void_p
        text = ctypes.create_unicode_buffer("Environment")
        send_message(
            ctypes.c_void_p(0xFFFF), 0x001A, 0,
            ctypes.cast(text, ctypes.c_void_p), 0x0002, 5000, None,
        )
    except (AttributeError, OSError):
        # O PATH do usuário já foi gravado; a notificação é apenas uma conveniência.
        pass


def update_windows_user_path(bin_dir: Path) -> str:
    try:
        import winreg

        key_path = r"Environment"
        with winreg.CreateKeyEx(
            winreg.HKEY_CURRENT_USER,
            key_path,
            0,
            winreg.KEY_QUERY_VALUE | winreg.KEY_SET_VALUE,
        ) as key:
            try:
                current, value_type = winreg.QueryValueEx(key, "Path")
            except FileNotFoundError:
                current, value_type = "", winreg.REG_EXPAND_SZ

            current = current or ""
            if path_contains(bin_dir, current):
                return "O diretório de instalação já está no PATH do usuário."

            updated = current.rstrip(";")
            updated = f"{updated};{bin_dir}" if updated else str(bin_dir)
            if value_type not in (winreg.REG_SZ, winreg.REG_EXPAND_SZ):
                value_type = winreg.REG_EXPAND_SZ
            winreg.SetValueEx(key, "Path", 0, value_type, updated)

        broadcast_windows_environment_change()
        return "PATH do usuário atualizado. Abra um novo terminal para usar o comando VNTextPatch."
    except OSError as exc:
        return f"Não foi possível atualizar o PATH do usuário automaticamente: {exc}"


def ensure_user_path(bin_dir: Path) -> str:
    if path_contains(bin_dir):
        return "O diretório do comando já está no PATH atual."
    try:
        if os.name == "nt":
            return update_windows_user_path(bin_dir)
        return update_posix_shell_path(bin_dir)
    except OSError as exc:
        return (
            f"A instalação foi concluída, mas não foi possível configurar o PATH: {exc}\n"
            f"Adicione manualmente este diretório ao PATH: {bin_dir}"
        )


def install(args: argparse.Namespace) -> int:
    platform_id, executable_name = detect_platform()
    tag_requested = args.version

    install_root = args.install_root.absolute()
    bin_dir = args.bin_dir.absolute()

    print(f"Plataforma detectada: {platform_id}")
    print("Consultando releases do GitHub...")
    release = get_release(tag_requested)
    tag = release["tag_name"]
    asset_name = f"VNTextPatch-{platform_id}-{tag}.zip"
    download_url, digest_field, checksum_url = find_assets(release, platform_id, tag)
    validate_asset_url(download_url, tag, asset_name)

    digest_from_api: str | None = None
    if digest_field and re.fullmatch(r"(?i)sha256:[0-9a-f]{64}", digest_field):
        digest_from_api = digest_field.split(":", 1)[1].lower()

    if digest_from_api is None:
        if not checksum_url:
            fail(
                f"O GitHub não forneceu o digest SHA-256 de {asset_name} nem o asset lateral "
                f"{asset_name}.sha256. Nenhum arquivo foi instalado."
            )
        validate_asset_url(checksum_url, tag, asset_name + ".sha256")

    version_dir = install_root / f"VNTextPatch-{tag}"
    launcher_name = f"{LINK_NAME}.cmd" if os.name == "nt" else LINK_NAME
    launcher = bin_dir / launcher_name

    install_root.mkdir(parents=True, exist_ok=True)
    bin_dir.mkdir(parents=True, exist_ok=True)
    install_root = install_root.resolve()
    bin_dir = bin_dir.resolve()
    version_dir = install_root / f"VNTextPatch-{tag}"
    launcher = bin_dir / launcher_name

    check_launcher_destination(launcher, install_root, executable_name)

    if version_dir.exists() or version_dir.is_symlink():
        executable_path = version_dir / executable_name
        if (
            not version_dir.is_dir()
            or executable_path.is_symlink()
            or not executable_path.is_file()
            or (os.name != "nt" and not os.access(executable_path, os.X_OK))
        ):
            fail(
                f"Já existe um diretório para {tag}, mas ele não contém uma instalação executável válida: "
                f"{version_dir}"
            )
        launcher = update_launcher(bin_dir, install_root, executable_path, executable_name)
        print(f"VNTextPatch {tag} já estava instalado.")
        print(f"Link/comando: {launcher} -> {executable_path}")
        if not args.no_path:
            print(ensure_user_path(bin_dir))
        return 0

    temporary_dir = Path(tempfile.mkdtemp(prefix=".VNTextPatch-install-", dir=install_root))
    published = False
    committed = False
    try:
        archive_path = temporary_dir / asset_name
        extract_dir = temporary_dir / "extracted"

        if digest_from_api is None:
            checksum_path = temporary_dir / f"{asset_name}.sha256"
            print("Baixando checksum SHA-256...")
            _, checksum_effective_url = download_file(checksum_url, checksum_path)
            validate_effective_url(checksum_effective_url)
            expected_digest = read_expected_checksum(checksum_path)
        else:
            expected_digest = digest_from_api

        print(f"Baixando VNTextPatch {tag} ({asset_name})...")
        actual_digest, effective_url = download_file(download_url, archive_path)
        validate_effective_url(effective_url)
        if actual_digest != expected_digest:
            fail(
                f"Falha na verificação SHA-256 de {asset_name}. "
                f"Esperado: {expected_digest}; obtido: {actual_digest}."
            )
        print("SHA-256 verificado.")

        expected_root = f"VNTextPatch-{platform_id}"
        extracted_executable = validate_and_extract(
            archive_path, extract_dir, expected_root, executable_name
        )
        extracted_root = extract_dir / expected_root

        if os.name != "nt":
            extracted_executable.chmod(extracted_executable.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)

        # O staging está dentro de install_root: o rename permanece no mesmo filesystem.
        os.replace(extracted_root, version_dir)
        published = True
        executable_path = version_dir / executable_name

        # A substituição do link/launcher ocorre somente depois da validação e publicação.
        launcher = update_launcher(bin_dir, install_root, executable_path, executable_name)
        committed = True

        print(f"VNTextPatch {tag} instalado com sucesso.")
        print(f"Versão: {version_dir}")
        print(f"Comando: {launcher}")
        if not args.no_path:
            print(ensure_user_path(bin_dir))
        print(f"Execute: {LINK_NAME} --help")
        return 0
    except OSError as exc:
        fail(f"Falha durante a instalação: {exc}")
    finally:
        shutil.rmtree(temporary_dir, ignore_errors=True)
        if published and not committed:
            # Se a ativação falhar, remove a versão recém-publicada; o launcher anterior fica intacto.
            shutil.rmtree(version_dir, ignore_errors=True)


def main() -> int:
    args = parse_arguments()
    try:
        return install(args)
    except InstallerError as exc:
        print(f"Erro: {exc}", file=sys.stderr)
        return 1
    except OSError as exc:
        print(f"Erro de acesso ao sistema de arquivos: {exc}", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        print("\nInstalação cancelada.", file=sys.stderr)
        return 130


if __name__ == "__main__":
    raise SystemExit(main())
