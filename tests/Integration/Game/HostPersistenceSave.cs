using System;
using System.IO;
using System.Linq;
using Queyang.Game;

namespace Queyang.Integration
{
    internal static class HostPersistenceSave
    {
        public static void Run(Action<string> record)
        {
            string root = Environment.GetEnvironmentVariable("QY_SMOKE_ROOT");
            if (string.IsNullOrEmpty(root) || !string.Equals(root, Util.GetKleiRootPath(), StringComparison.OrdinalIgnoreCase) ||
                !CoopSession.IsHosting)
                throw new InvalidOperationException("Requires an isolated, active host.");
            string dir = Path.Combine(root, "OxygenNotIncluded", "save_files", "QyPersistence");
            string save = Path.Combine(dir, "QyPersistence.sav");
            if (File.Exists(save)) throw new IOException("Refusing to replace a persistence fixture.");
            Directory.CreateDirectory(dir);
            var schedule = ScheduleManager.Instance.GetSchedules().First();
            string name = schedule.name;
            string marker = "QA-" + Guid.NewGuid().ToString("N");
            try
            {
                schedule.name = marker;
                SaveLoader.Instance.Save(save, false, true);
                if (!File.Exists(save) || new FileInfo(save).Length == 0 || !CoopSession.IsHosting)
                    throw new IOException("Native save missing or room ended on save.");
                File.WriteAllText(Path.Combine(dir, "marker.txt"), marker);
                record("PERSIST SAVED native manual save while hosting; bytes=" + new FileInfo(save).Length);
            }
            finally { schedule.name = name; }
        }
    }
}
