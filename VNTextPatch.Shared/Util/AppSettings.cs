using Microsoft.Extensions.Configuration;
using System.IO;


namespace VNTextPatch.Shared.Util
{ 
    internal static class AppSettings
    {
        public static readonly IConfiguration Configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddXmlFile("App.config", optional: false, reloadOnChange: true)
            .Build();
    }
}
