using System;
using Crestron.SimplSharp.CrestronIO;
using Newtonsoft.Json;

namespace PifflersCrestronLibrary.Helper
{
    /// <summary>
    /// Generischer JSON-Konfigurationslader fuer Crestron-Projekte.
    /// Muster (analog Projekt Lindt/Pausenhalle): Vor dem Laden
    /// <see cref="SetConfigurationPath"/> aufrufen, dann <see cref="Load{T}"/>.
    /// Projekte definieren eine eigene Config-POCO und rufen z.B.:
    ///   ConfigurationLoader.SetConfigurationPath("\\user\\config.json");
    ///   var cfg = ConfigurationLoader.Load&lt;MyConfig&gt;();
    /// Projektspezifische Logausgabe (LogSummary o.ae.) bleibt beim Aufrufer.
    /// </summary>
    public static class ConfigurationLoader
    {
        private static string _configPath = "\\user\\config.json";

        public static string ConfigPath { get { return _configPath; } }

        public static void SetConfigurationPath(string path)
        {
            if (!string.IsNullOrEmpty(path)) _configPath = path;
        }

        /// <summary>Laedt und deserialisiert die Konfiguration nach T. Wirft bei Fehler.</summary>
        public static T Load<T>() where T : class
        {
            if (!File.Exists(_configPath))
                throw new FileNotFoundException("Konfiguration nicht gefunden: " + _configPath);

            string json;
            using (var reader = new StreamReader(_configPath))
            {
                json = reader.ReadToEnd();
            }

            var config = JsonConvert.DeserializeObject<T>(json);
            if (config == null)
                throw new InvalidOperationException("Konfiguration konnte nicht deserialisiert werden: " + _configPath);

            return config;
        }
    }
}
