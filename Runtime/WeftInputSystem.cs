#if WEFT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;

namespace Weft
{
    /// <summary>
    /// The Input System's keys for the Views. Extension methods: an assembly that does not reference the Input System
    /// still calls WeftView.KeyUsed with a code or a KeyCode without needing it.
    /// </summary>
    public static class WeftInputSystem
    {
        /// <summary>The KeyboardEvent.code of an Input System key (null for one that is not a key the Views read).</summary>
        public static string WeftCode(this Key key)
        {
            var index = WeftKeys.IndexOfSystemKey(key);
            return index < 0 ? null : WeftKeys.CodeAt(index);
        }

        /// <summary>Whether the page used an Input System key this frame (WeftView.KeyUsed).</summary>
        public static bool KeyUsed(this WeftView view, Key key)
        {
            return view.KeyUsedAt(WeftKeys.IndexOfSystemKey(key));
        }
    }
}
#endif
