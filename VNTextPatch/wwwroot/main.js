import { dotnet } from './_framework/dotnet.js';

const { getAssemblyExports, getConfig } = await dotnet
  .withConsoleForwarding() // redireciona Console.WriteLine pro devtools
  .create();

dotnet.run();
