﻿using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace Braver {

    public abstract class DataSource {
        public abstract Stream TryOpen(string file);
        public abstract IEnumerable<string> Scan();
    }

    public class LocalPref {
        [XmlAttribute]
        public string Name { get; set; }
        [XmlAttribute]
        public string Value{ get; set; }
    }
    public class LocalPrefs {
        [XmlElement("Pref")]
        public List<LocalPref> Prefs { get; set; } = new();
    }

    public class FGame {

        private class LGPDataSource : DataSource {
            private Ficedula.FF7.LGPFile _lgp;

            public LGPDataSource(Ficedula.FF7.LGPFile lgp) {
                _lgp = lgp;
            }

            public override IEnumerable<string> Scan() => _lgp.Filenames;
            public override Stream TryOpen(string file) => _lgp.TryOpen(file);
        }

        private class FileDataSource : DataSource {
            private string _root;

            public FileDataSource(string root) {
                _root = root;
            }

            public override IEnumerable<string> Scan() {
                //TODO subdirectories
                return Directory.GetFiles(_root).Select(s => Path.GetFileName(s));
            }

            public override Stream TryOpen(string file) {
                string fn = Path.Combine(_root, file);
                if (File.Exists(fn))
                    return new FileStream(fn, FileMode.Open, FileAccess.Read);
                return null;
            }
        }

        private Stack<Screen> _screens = new();

        public VMM Memory { get; } = new();
        public SaveMap SaveMap { get; } 

        public Audio Audio { get; }
        public Screen Screen => _screens.Peek();

        public SaveData SaveData { get; private set; }
        private Dictionary<string, List<DataSource>> _data = new Dictionary<string, List<DataSource>>(StringComparer.InvariantCultureIgnoreCase);
        private Dictionary<Type, object> _singletons = new Dictionary<Type, object>();

        private Dictionary<string, string> _prefs;

        public FGame(string data, string bdata) {
            SaveMap = new SaveMap(Memory);

            _data["field"] = new List<DataSource> {
                new LGPDataSource(new Ficedula.FF7.LGPFile(Path.Combine(data, "field", "flevel.lgp"))),
                new LGPDataSource(new Ficedula.FF7.LGPFile(Path.Combine(data, "field", "char.lgp"))),
            };
            _data["menu"] = new List<DataSource> {
                new LGPDataSource(new Ficedula.FF7.LGPFile(Path.Combine(data, "menu", "menu_us.lgp"))),
            };
            _data["wm"] = new List<DataSource> {
                new LGPDataSource(new Ficedula.FF7.LGPFile(Path.Combine(data, "wm", "world_us.lgp"))),
                new FileDataSource(Path.Combine(data, "wm"))
            };
            foreach (string dir in Directory.GetDirectories(bdata)) {
                string category = Path.GetFileName(dir);
                if (!_data.TryGetValue(category, out var L))
                    L = _data[category] = new();
                L.Add(new FileDataSource(dir));
            }

            Audio = new Audio(data);

            Audio.Precache(Sfx.Cursor, true);
            Audio.Precache(Sfx.Cancel, true);
            Audio.Precache(Sfx.Invalid, true);

            string prefs = GetPrefsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(prefs));
            if (File.Exists(prefs)) {
                using (var fs = File.OpenRead(prefs)) {
                    var lp = Serialisation.Deserialise<LocalPrefs>(fs);
                    _prefs = lp.Prefs
                        .ToDictionary(p => p.Name, p => p.Value, StringComparer.InvariantCultureIgnoreCase);
                }
            } else
                _prefs = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);

        }

        private static string GetPrefsPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Braver", "prefs.xml");

        public string GetPref(string name, string def = "") {
            _prefs.TryGetValue(name, out string v);
            return v ?? def;
        }
        public void SetPref(string name, string value) {
            _prefs[name] = value;
            using (var fs = File.OpenWrite(GetPrefsPath())) {
                var lp = new LocalPrefs {
                    Prefs = _prefs.Select(kv => new LocalPref { Name = kv.Key, Value = kv.Value }).ToList()
                };
                Serialisation.Serialise(lp, fs);
            }
        }

        public T Singleton<T>(Func<T> create) {
            if (_singletons.TryGetValue(typeof(T), out object obj))
                return (T)obj;
            else {
                T t = create();
                _singletons[typeof(T)] = t;
                return t;
            }                
        }

        public void NewGame() {
            using (var s = Open("save", "newgame.xml"))
                SaveData = Serialisation.Deserialise<SaveData>(s);
            Memory.ResetAll();
            Braver.NewGame.Init(this);
            SaveData.Loaded();
        }

        public void Save(string path) {
            using (var fs = File.OpenWrite(path + ".sav"))
                Serialisation.Serialise(SaveData, fs);
            using (var fs = File.OpenWrite(path + ".mem"))
                Memory.Save(fs);
        }

        public void Load(string path) {
            using (var fs = File.OpenRead(path + ".mem"))
                Memory.Load(fs);
            using (var fs = File.OpenRead(path + ".sav"))
                SaveData = Serialisation.Deserialise<SaveData>(fs);
            SaveData.Loaded();
        }

        public Stream TryOpen(string category, string file) {
            foreach (var source in _data[category]) {
                var s = source.TryOpen(file);
                if (s != null)
                    return s;
            }
            return null;
        }

        public Stream Open(string category, string file) {
            var s = TryOpen(category, file);
            if (s == null)
                throw new F7Exception($"Could not open {category}/{file}");
            else
                return s;
        }
        public string OpenString(string category, string file) {
            using(var s = Open(category, file)) {
                using (var sr = new StreamReader(s))
                    return sr.ReadToEnd();
            }
        }

        public IEnumerable<string> ScanData(string category) {
            if (_data.TryGetValue(category, out var sources))
                return sources.SelectMany(s => s.Scan());
            else
                return Enumerable.Empty<string>();
        }

        public void PushScreen(Screen s) {
            _screens.Push(s);
        }

        public void PopScreen(Screen current) {
            Debug.Assert(_screens.Pop() == current);
            current.Dispose();
            Screen.Reactivated();
        }

        public void ChangeScreen(Screen from, Screen to) {
            _screens.TryPeek(out var current);
            Debug.Assert(from == current);
            if (current != null) {
                _screens.Pop();
                current.Dispose();
            }
            _screens.Push(to);
        }

        public void InvokeOnMainThread(Action a) {
            _invoke.Add(a);
        }

        private List<Action> _invoke = new();
        private int _lastSeconds;
        public void Step(GameTime gameTime, InputState input) {
            if (Screen.InputEnabled)
                Screen.ProcessInput(input);

            if ((int)gameTime.TotalGameTime.TotalSeconds != _lastSeconds) {
                _lastSeconds = (int)gameTime.TotalGameTime.TotalSeconds;
                SaveData.GameTimeSeconds++;
            }

            var actions = _invoke.ToArray();
            _invoke.Clear();
            foreach (var action in actions)
                action();

            Screen.Step(gameTime);

        }

    }
}
