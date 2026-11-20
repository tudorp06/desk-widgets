using System;
using System.Collections.Generic;
using System.Linq;

namespace DeskWidgets
{
    // Named sets of widgets. A profile is not a separate file: every widget section carries a
    // "profile" key and only the active profile's sections are opened. A section with no key at
    // all belongs to Default, so a widgets.ini written before profiles existed keeps working.
    //
    // The point of them: removing a widget while "Work" is active deletes a Work section and
    // nothing else, so switching back to "Gaming" finds its widgets where they were left.
    static class Profiles
    {
        public const string DefaultName = "Default";
        const string Key = "profile";
        const int MaxNameLength = 40;

        // INI stores one value per line, and the name list is pipe-separated, so these can't
        // appear in a name.
        static readonly char[] Forbidden = { '=', '|', '[', ']', '\r', '\n' };

        static Config Store { get { return App.Current == null ? null : App.Current.Store; } }

        public static string Active
        {
            get
            {
                if (App.Current == null) return DefaultName;
                string v = App.Current.ActiveProfileName;
                return v.Length == 0 ? DefaultName : v;
            }
        }

        // Which profile a section belongs to.
        public static string Of(Section s)
        {
            string v = s.Get(Key, "");
            return v.Length == 0 ? DefaultName : v;
        }

        // Widget sections of the active profile ([app] and other profiles' sections are not ours).
        public static bool Belongs(Section s)
        {
            return s.Type.Length > 0 && Same(Of(s), Active);
        }

        static bool Same(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

        // Default first, then the rest alphabetically. The stored list is what lets an empty
        // profile exist; names found on sections are folded in so a restored backup can't lose one.
        public static IList<string> Names
        {
            get
            {
                var seen = new List<string> { DefaultName };
                Action<string> add = delegate(string n)
                {
                    if (n.Length > 0 && !seen.Any(x => Same(x, n))) seen.Add(n);
                };
                if (App.Current != null)
                    foreach (string n in App.Current.ProfileList.Split('|')) add(n.Trim());
                if (Store != null)
                    foreach (var s in Store.Sections)
                        if (s.Type.Length > 0) add(Of(s));
                add(Active);
                return seen.Take(1).Concat(seen.Skip(1).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)).ToList();
            }
        }

        public static bool Exists(string name)
        {
            return Names.Any(n => Same(n, name));
        }

        // null when the name is usable, otherwise why it isn't -- shown to the user as-is.
        public static string Validate(string name)
        {
            if (name == null) return "Enter a name.";
            name = name.Trim();
            if (name.Length == 0) return "Enter a name.";
            if (name.Length > MaxNameLength) return "That name is too long.";
            if (name.IndexOfAny(Forbidden) >= 0) return "A name can't contain = | [ or ].";
            if (Exists(name)) return "There's already a profile called “" + name + "”.";
            return null;
        }

        static void SaveList(IEnumerable<string> names)
        {
            if (App.Current != null) App.Current.ProfileList = string.Join("|", names.ToArray());
        }

        // A new, empty profile: switching to it leaves a clear desktop to put widgets on.
        public static void Create(string name)
        {
            name = name.Trim();
            SaveList(Names.Concat(new[] { name }));
            SwitchTo(name);
        }

        // Same widgets, same places, same settings, under a new name.
        public static void Duplicate(string name)
        {
            name = name.Trim();
            var store = Store;
            if (store == null) return;

            var copies = new List<Section>();
            foreach (var s in store.Sections.Where(Belongs).ToList())
            {
                var copy = store.Add(s.Type);
                foreach (var kv in s.Values) copy.Values[kv.Key] = kv.Value;
                copy.Values[Key] = name;
                copies.Add(copy);
            }
            store.Save();
            SaveList(Names.Concat(new[] { name }));
            SwitchTo(name);
        }

        public static void Rename(string from, string to)
        {
            to = to.Trim();
            var store = Store;
            if (store == null || Same(from, to)) return;

            foreach (var s in store.Sections)
                if (s.Type.Length > 0 && Same(Of(s), from)) s.Values[Key] = to;
            SaveList(Names.Select(n => Same(n, from) ? to : n));
            if (Same(Active, from) && App.Current != null) App.Current.ActiveProfileName = to;
            store.Save();
            App.Current.RefreshAfterProfileChange();
        }

        // Forgets the profile and every widget in it. The caller confirms first.
        public static void Delete(string name)
        {
            var store = Store;
            if (store == null) return;
            var left = Names.Where(n => !Same(n, name)).ToList();
            if (left.Count == 0) return; // never leave the app with no profile at all

            bool wasActive = Same(Active, name);
            if (wasActive) SwitchTo(left[0]);

            foreach (var s in store.Sections.Where(s => s.Type.Length > 0 && Same(Of(s), name)).ToList())
                store.Remove(s);
            SaveList(left);
        }

        public static void SwitchTo(string name)
        {
            if (App.Current != null) App.Current.SwitchProfile(name.Trim());
        }
    }
}
