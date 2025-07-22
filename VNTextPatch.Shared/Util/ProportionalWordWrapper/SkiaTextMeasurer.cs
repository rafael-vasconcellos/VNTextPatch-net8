using System;
using System.Collections.Generic;
using VNTextPatch.Shared.Util;
using SkiaSharp;


namespace VNTextPatch.Shared.Util
{
    internal class SkiaTextMeasurer : ITextMeasurer, IDisposable
    {
        private readonly SkiaSharp.SKPaint _paint;
        private readonly Dictionary<char, float> _charWidthCache = new Dictionary<char, float>();
        private readonly Dictionary<int, float> _kernAmounts = new Dictionary<int, float>();
        private readonly bool _cacheEnabled;

        public SkiaTextMeasurer(string fontName, int fontSize, bool bold, int lineWidth)
        {
            _paint = new SkiaSharp.SKPaint
            {
                Typeface = SkiaSharp.SKTypeface.FromFamilyName(
                    fontName,
                    bold ? SkiaSharp.SKFontStyleWeight.Bold : SkiaSharp.SKFontStyleWeight.Normal,
                    SkiaSharp.SKFontStyleWidth.Normal,
                    SkiaSharp.SKFontStyleSlant.Upright),
                TextSize = fontSize,
                IsAntialias = true,
                HintingLevel = SkiaSharp.SKPaintHinting.Normal
            };

            _cacheEnabled = true;

            if (_cacheEnabled)
            {
                PreCacheCommonCharacters();
                PreCalculateKerning();
            }
        }

        protected override int GetTextWidth(string text, int offset, int length)
        {
            if (length <= 0) return 0;

            string substring = text.Substring(offset, length);

            // SkiaSharp já aplica kerning automaticamente
            return (int)Math.Ceiling(_paint.MeasureText(substring));
        }

        protected override int LineWidth
        {
            get;
        }

        public override void Dispose()
        {
            _paint?.Dispose();
        }

        private float GetCharWidthDirect(char c)
        {
            return _paint.MeasureText(c.ToString());
        }

        private int GetCharWidth(char c)
        {
            if (_cacheEnabled && _charWidthCache.TryGetValue(c, out float cachedWidth))
            {
                return (int)Math.Ceiling(cachedWidth);
            }

            float width = _paint.MeasureText(c.ToString());

            if (_cacheEnabled)
            {
                _charWidthCache[c] = width;
            }

            return (int)Math.Ceiling(width);
        }

        private int GetKernAmount(char first, char second)
        {
            //if (!_cacheEnabled) return 0;

            int key = first | (second << 16);
            if (_kernAmounts.TryGetValue(key, out float kern))
            {
                return (int)Math.Round(kern);
            }

            // Para pares não cached, calcular on-demand
            string pair = $"{first}{second}";
            float pairWidth = _paint.MeasureText(pair);
            float individualSum = GetCharWidthDirect(first) + GetCharWidthDirect(second);
            float kernAmount = pairWidth - individualSum;

            _kernAmounts[key] = kernAmount;
            return (int)Math.Round(kernAmount);
        }

        private void PreCacheCommonCharacters()
        {
            // Cache caracteres ASCII mais comuns
            for (char c = ' '; c <= '~'; c++)
            {
                _charWidthCache[c] = _paint.MeasureText(c.ToString());
            }

            // Cache alguns caracteres especiais comuns
            char[] specialChars = { 'á', 'à', 'ã', 'â', 'é', 'ê', 'í', 'ó', 'ô', 'õ', 'ú', 'ç', 'ñ' };
            foreach (char c in specialChars)
            {
                _charWidthCache[c] = _paint.MeasureText(c.ToString());
            }
        }

        private void PreCalculateKerning()
        {
            // SkiaSharp automaticamente aplica kerning ao medir texto completo
            // Para simular kerning individual, comparamos medição de pares vs soma individual

            string[] commonPairs = { "AV", "AW", "AY", "VA", "WA", "YA", "To", "Tr", "Ta", "Te", "Ti", "Ty", "We", "Wo" };

            foreach (string pair in commonPairs)
            {
                if (pair.Length == 2)
                {
                    float pairWidth = _paint.MeasureText(pair);
                    float individualSum = GetCharWidthDirect(pair[0]) + GetCharWidthDirect(pair[1]);
                    float kernAmount = pairWidth - individualSum;

                    int key = pair[0] | (pair[1] << 16);
                    _kernAmounts[key] = kernAmount;
                }
            }
        }

    }
}