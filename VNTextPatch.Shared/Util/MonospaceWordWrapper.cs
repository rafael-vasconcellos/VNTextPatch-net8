﻿using System;
using VNTextPatch.Shared.Util;
using Microsoft.Extensions.Configuration;
//using System.Configuration;

namespace VNTextPatch.Shared.Util
{
    internal class MonospaceWordWrapper : WordWrapper
    {
        public static readonly MonospaceWordWrapper Default = new MonospaceWordWrapper();

        private MonospaceWordWrapper()
            : this(Convert.ToInt32(AppSettings.Configuration["MonospaceCharactersPerLine"]))
        {
        }

        public MonospaceWordWrapper(int charactersPerLine)
        {
            LineWidth = charactersPerLine;
        }

        protected override int GetTextWidth(string text, int offset, int length)
        {
            return length;
        }

        protected override int LineWidth
        {
            get;
        }
    }
}
