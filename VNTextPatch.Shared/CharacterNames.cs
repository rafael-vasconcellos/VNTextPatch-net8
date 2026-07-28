using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VNTextPatch.Shared.Util;
using System.Xml.Linq;


namespace VNTextPatch.Shared
{
    public class CharacterNames
    {
        private static readonly Lazy<CharacterNames> _instance = new Lazy<CharacterNames>(() => new CharacterNames());

        private static CharacterNames Instance
        {
            get { return _instance.Value; }
        }

        public static string GetTranslation(string japaneseName)
        {
            if (!Instance._translations.TryGetValue(japaneseName, out var englishName))
            {
                Instance._translations.Add(japaneseName, japaneseName);
                return japaneseName;
            }
            return englishName;
        }

        public static void Save()
        {
            Instance.Write();
        }

        private static string FilePath
        {
            get
            {
                //var directory = Directory.GetCurrentDirectory();
                var directory = Environment.CurrentDirectory;
                return Path.Combine(directory, "names.xml");
            }
        }

        private readonly Dictionary<string, string> _translations;

        private CharacterNames()
        {
            try
            {
                XDocument doc = XDocument.Load(FilePath);
                _translations = doc.Root?.Elements("n")
                    .ToDictionary(
                        e => (string)e.Element("o")!,
                        e => (string)e.Element("tl")!
                    ) ?? new Dictionary<string, string>();

            } catch
            {
                _translations = new Dictionary<string, string>();
            }
        }

        private void Write()
        {
            if (_translations.Count == 0)
                return;
            var doc = new XDocument(
                new XElement("names",
                    _translations.Select(n =>
                        new XElement("n",
                            new XElement("o", n.Key),
                            new XElement("tl", n.Value)))));
            doc.Save(FilePath);
        }

    }

}
