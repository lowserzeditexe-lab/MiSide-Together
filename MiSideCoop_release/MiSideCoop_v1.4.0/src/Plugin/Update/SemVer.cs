using System;

namespace MiSideCoop.Update
{
    /// <summary>
    /// Comparateur de versions style <c>major.minor.patch</c>.
    /// Tolérant : accepte des préfixes "v" et des suffixes ignorés.
    /// </summary>
    internal readonly struct SemVer : IComparable<SemVer>
    {
        public readonly int Major;
        public readonly int Minor;
        public readonly int Patch;

        public SemVer(int major, int minor, int patch)
        {
            Major = major; Minor = minor; Patch = patch;
        }

        public static bool TryParse(string s, out SemVer version)
        {
            version = default;
            if (string.IsNullOrWhiteSpace(s)) return false;

            // Nettoie : "v1.2.9" → "1.2.9", "1.2.9-beta" → "1.2.9"
            var clean = s.Trim();
            if (clean.Length > 0 && (clean[0] == 'v' || clean[0] == 'V'))
                clean = clean.Substring(1);
            var dashIdx = clean.IndexOf('-');
            if (dashIdx >= 0) clean = clean.Substring(0, dashIdx);
            var plusIdx = clean.IndexOf('+');
            if (plusIdx >= 0) clean = clean.Substring(0, plusIdx);

            var parts = clean.Split('.');
            if (parts.Length < 1) return false;

            int maj = 0, min = 0, pat = 0;
            if (!int.TryParse(parts[0], out maj)) return false;
            if (parts.Length >= 2 && !int.TryParse(parts[1], out min)) return false;
            if (parts.Length >= 3 && !int.TryParse(parts[2], out pat)) return false;

            version = new SemVer(maj, min, pat);
            return true;
        }

        public int CompareTo(SemVer other)
        {
            if (Major != other.Major) return Major.CompareTo(other.Major);
            if (Minor != other.Minor) return Minor.CompareTo(other.Minor);
            return Patch.CompareTo(other.Patch);
        }

        public override string ToString() => $"{Major}.{Minor}.{Patch}";
    }
}
