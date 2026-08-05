using System;

namespace DesktopSwitcher
{
    /// <summary>
    /// Immutable snapshot of a single virtual desktop.
    ///
    /// Id is identity. Index is display-only: removing a desktop renumbers everything
    /// after it, so an index captured before an async notification arrives can easily
    /// refer to a different desktop by the time it is used. Anything that mutates state
    /// takes a Guid.
    /// </summary>
    sealed class Desktop
    {
        readonly Guid _id;
        readonly int _index;
        readonly string _name;
        readonly bool _isCurrent;

        public Desktop(Guid id, int index, string name, bool isCurrent)
        {
            _id = id;
            _index = index;
            _name = name;
            _isCurrent = isCurrent;
        }

        /// <summary>Stable identity. Survives renumbering.</summary>
        public Guid Id { get { return _id; } }

        /// <summary>Zero-based position. Display only - not stable across removals.</summary>
        public int Index { get { return _index; } }

        /// <summary>User-assigned name, or null if the desktop has never been renamed.</summary>
        public string Name { get { return _name; } }

        public bool IsCurrent { get { return _isCurrent; } }

        /// <summary>One-based number shown on the button face.</summary>
        public int Number { get { return _index + 1; } }

        /// <summary>Name if set, otherwise "Desktop N". Used for tooltips later.</summary>
        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(_name)) return _name;
                return "Desktop " + Number;
            }
        }

        public override string ToString()
        {
            return string.Format("[{0}] {1} {2}{3}",
                Number, DisplayName, _id.ToString().Substring(0, 8),
                _isCurrent ? " *current*" : "");
        }
    }
}
