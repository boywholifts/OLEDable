using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

namespace OLEDable
{
    /// <summary>One export waiting its turn: the settings as they were when it was queued, and where it goes.</summary>
    [DataContract]
    public class QueuedExport
    {
        public const string Waiting = "waiting", Interrupted = "interrupted", Failed = "failed";

        [DataMember] public string Key;
        [DataMember] public EditSession Session;
        [DataMember] public string OutPath;
        /// <summary><see cref="Waiting"/>, <see cref="Interrupted"/> (OLEDable closed during it) or <see cref="Failed"/>.</summary>
        [DataMember] public string State;
        [DataMember] public string Error;
        // read from the source when the edit was opened; EditSession doesn't save them
        [DataMember] public double Duration, Fps;
        [DataMember] public int Width, Height;

        public string Title { get { return Session.Title ?? Path.GetFileNameWithoutExtension(OutPath); } }

        public static QueuedExport For(EditSession snap, string outPath)
        {
            return new QueuedExport
            {
                Key = Guid.NewGuid().ToString("N"), Session = snap, OutPath = outPath, State = Waiting,
                Duration = snap.Duration, Fps = snap.Fps, Width = snap.Width, Height = snap.Height,
            };
        }

        /// <summary>The session ready to export (its source details filled back in).</summary>
        public EditSession Ready()
        {
            Session.Duration = Duration; Session.Fps = Fps; Session.Width = Width; Session.Height = Height;
            return Session;
        }
    }

    /// <summary>
    /// The exports waiting to run, one after another. Kept in the app's data folder (queue.json), so a queue
    /// survives closing OLEDable: it starts again by itself (a setting) or waits for "Start".
    /// </summary>
    public class ExportQueue
    {
        readonly string path;
        readonly List<QueuedExport> items = new List<QueuedExport>();
        public event Action Changed;

        public ExportQueue(string dataDir)
        {
            path = Path.Combine(dataDir, "queue.json");
            try
            {
                if (File.Exists(path))
                {
                    var saved = Json.Read<List<QueuedExport>>(File.ReadAllText(path));
                    if (saved != null) items.AddRange(saved.Where(i => i != null && i.Session != null && i.OutPath != null));
                }
            }
            catch (Exception ex) { Tools.LogError("reading the export queue", ex); }
        }

        public IReadOnlyList<QueuedExport> Items { get { return items; } }
        public int Count { get { return items.Count; } }

        /// <summary>The next export to run, or null.</summary>
        public QueuedExport Next { get { return items.FirstOrDefault(i => i.State == QueuedExport.Waiting); } }

        /// <summary>
        /// Adds an export. If the same video is already waiting, its entry is updated instead (newest settings,
        /// same place in line), so it isn't exported twice.
        /// </summary>
        public void Add(QueuedExport q, QueuedExport running)
        {
            var same = items.FirstOrDefault(i => i != running && i.Session.Id == q.Session.Id);
            if (same != null) items[items.IndexOf(same)] = q;
            else items.Add(q);
            Save();
        }

        public void Remove(QueuedExport q) { if (items.Remove(q)) Save(); }

        public void Clear() { items.Clear(); Save(); }

        public void Set(QueuedExport q, string state, string error = null)
        {
            q.State = state; q.Error = error;
            Save();
        }

        /// <summary>Everything stopped or failed goes back in line.</summary>
        public void Requeue()
        {
            foreach (var i in items) { i.State = QueuedExport.Waiting; i.Error = null; }
            Save();
        }

        /// <summary>Whether a file name is already promised to a queued export.</summary>
        public bool Claims(string outPath)
        {
            return items.Any(i => string.Equals(i.OutPath, outPath, StringComparison.OrdinalIgnoreCase));
        }

        public void Save()
        {
            try
            {
                if (items.Count == 0) Tools.TryDelete(path);
                else Tools.WriteAtomic(path, Json.Write(items));
            }
            catch (Exception ex) { Tools.LogError("saving the export queue", ex); }
            Changed?.Invoke();
        }
    }
}
