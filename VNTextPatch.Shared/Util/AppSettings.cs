using System.IO;
using Microsoft.Extensions.Configuration;


namespace VNTextPatch.Shared.Util
{
    internal static class AppSettings
    {
        public static readonly IConfiguration Configuration = Build();

        public static IConfiguration Build()
        {
            try
            {
                return new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddXmlFile("settings.xml", optional: true, reloadOnChange: true)
                    .Build();

            }
            catch (Exception e)
            {
                Console.Write(e);
                throw; // Preserva a stack trace
            }

        }
    }
}
