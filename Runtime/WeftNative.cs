using System;
using System.Runtime.InteropServices;

namespace Weft
{
    // The C functions of weft.dll (Native~/include/weft_unity.h and weft's include/weft/weft.h), and their structs.
    internal static unsafe class WeftNative
    {
        const string Dll = "weft";

        public const uint AbiVersion = 1;
        public const uint RuntimeSystemFonts = 1;
        public const uint PageCrpgBridge = 1;
        public const uint PageForgivingCss = 2;
        public const uint PageWebPlatform = 4;
        public const uint PageSpatialNavigation = 8;
        public const int GamepadSlots = 4;
        public const int GamepadButtons = 17;
        public const int GamepadAxes = 4;
        public const uint ValueJson = 2;
        public const uint EmitLatest = 1;
        public const uint KeyRepeat = 1;
        public const uint KeyIme = 2;
        public const uint EffectNumber = 1;
        public const uint EffectVec2 = 2;
        public const uint EffectVec3 = 3;
        public const uint EffectVec4 = 4;
        public const uint EffectColor = 5;
        public const uint EffectTextureData = 1;
        public const uint EffectTextureSrgb = 2;
        public const uint EffectTexturePng = 0x100;
        public const uint BuildDevTools = 1;
        public const uint DevToolsAllInterfaces = 1;

        public enum Result
        {
            Ok = 0,
            InvalidArgument = 1,
            UnsupportedAbi = 2,
            OutOfMemory = 3,
            WrongThread = 4,
            Busy = 5,
            StaleHandle = 6,
            CapacityExceeded = 7,
            TimeRegression = 8,
            InternalError = 9,
            ScriptError = 10,
            Faulted = 11,
            Unavailable = 12,
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ViewHandle
        {
            public ulong RuntimeId;
            public uint Slot;
            public uint Generation;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ViewDesc
        {
            public uint StructSize;
            public uint Reserved;
            public float LogicalWidth;
            public float LogicalHeight;
            public float DeviceScale;
            public uint Reserved2;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Script
        {
            public byte* Name;
            public UIntPtr NameSize;
            public byte* Source;
            public UIntPtr SourceSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PageDesc
        {
            public uint StructSize;
            public uint Flags;
            public Script* Scripts;
            public uint ScriptCount;
            public uint Reserved;
            public byte* Stylesheet;
            public UIntPtr StylesheetSize;
            public byte* Keyframes;
            public UIntPtr KeyframesSize;
            public byte* Root;
            public UIntPtr RootSize;
            public byte* Base;
            public UIntPtr BaseSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FontFace
        {
            public uint StructSize;
            public uint Weight;
            public uint Italic;
            public uint FaceIndex;
            public byte* Family;
            public UIntPtr FamilySize;
            public byte* Path;
            public UIntPtr PathSize;
            public byte* UnicodeRange;
            public UIntPtr UnicodeRangeSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct EffectParameter
        {
            public byte* Name;
            public UIntPtr NameSize;
            public byte* Initial;
            public UIntPtr InitialSize;
            public uint Type;
            public uint Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct EffectTexture
        {
            public byte* Name;
            public UIntPtr NameSize;
            public byte* Bytes;
            public UIntPtr Size;
            public uint Format;
            public uint Width;
            public uint Height;
            public uint Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct EffectDesc
        {
            public uint StructSize;
            public uint Animated;
            public byte* Name;
            public UIntPtr NameSize;
            public EffectParameter* Parameters;
            public EffectTexture* Textures;
            public uint ParameterCount;
            public uint TextureCount;
            public byte* DxbcEncoded;
            public UIntPtr DxbcEncodedSize;
            public byte* DxbcLinear;
            public UIntPtr DxbcLinearSize;
            public byte* SpirvEncoded;
            public UIntPtr SpirvEncodedSize;
            public byte* SpirvLinear;
            public UIntPtr SpirvLinearSize;
            public uint Backdrop;
            public float BackdropMargin;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BuildInfo
        {
            public uint StructSize;
            public uint AbiVersion;
            public uint Flags;
            public uint Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DevToolsDesc
        {
            public uint StructSize;
            public uint Flags;
            public uint Port;
            public uint Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DevToolsInfo
        {
            public uint StructSize;
            public uint Port;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ViewFrameInfo
        {
            public uint StructSize;
            public uint Animated;
            public ulong Revision;
            public double TimeSeconds;
            public float LogicalWidth;
            public float LogicalHeight;
            public float DeviceScale;
            public uint Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DrawTime
        {
            public uint Slot;
            public uint Generation;
            public double Seconds;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void StorageItemFn(IntPtr user, byte* key, UIntPtr keySize, byte* value, UIntPtr valueSize);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void TextFn(IntPtr user, uint ok, byte* text, UIntPtr textSize);

        [StructLayout(LayoutKind.Sequential)]
        public struct PointerEvent
        {
            public uint StructSize;
            public uint Kind;
            public float X;
            public float Y;
            public uint Buttons;
            public uint Modifiers;
            public float WheelX;
            public float WheelY;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TextInput
        {
            public uint StructSize;
            public uint Active;
            public uint Composing;
            public uint Password;
            public float CaretX;
            public float CaretY;
            public float CaretWidth;
            public float CaretHeight;
            public float FieldX;
            public float FieldY;
            public float FieldWidth;
            public float FieldHeight;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KeyEvent
        {
            public uint StructSize;
            public uint Kind;
            public byte* Key;
            public UIntPtr KeySize;
            public byte* Code;
            public UIntPtr CodeSize;
            public uint Modifiers;
            public uint Flags;
            public uint KeyCode;
            public uint Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct GamepadState
        {
            public uint StructSize;
            public uint Index;
            public uint Connected;
            public uint Pressed;
            public uint Touched;
            public uint Reserved;
            public byte* Id;
            public UIntPtr IdSize;
            public fixed float Buttons[GamepadButtons];
            public fixed float Axes[GamepadAxes];
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Hit
        {
            public uint StructSize;
            public uint Element;
            public fixed byte Cursor[32];
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Message
        {
            public uint Kind;
            public uint Reserved;
            public ulong CallId;
            public byte* Name;
            public UIntPtr NameSize;
            public byte* Json;
            public UIntPtr JsonSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RenderStats
        {
            public uint Renderer;
            public uint Reserved;
            public ulong Draws;
            public ulong Failures;
            public fixed byte LastFailure[256];
        }

        [DllImport(Dll)] public static extern IntPtr weft_unity_build();
        [DllImport(Dll)] public static extern Result weft_unity_start(uint flags, uint maxViews);
        [DllImport(Dll)] public static extern void weft_unity_stop();
        [DllImport(Dll)] public static extern IntPtr weft_unity_runtime();
        [DllImport(Dll)] public static extern Result weft_unity_add_font(FontFace* face);
        [DllImport(Dll)] public static extern Result weft_unity_set_generic_family(byte* generic, UIntPtr genericSize, byte* family, UIntPtr familySize);
        [DllImport(Dll)] public static extern Result weft_unity_add_fallback_family(byte* family, UIntPtr familySize);
        [DllImport(Dll)] public static extern Result weft_unity_add_effect(EffectDesc* effect);
        [DllImport(Dll)] public static extern Result weft_unity_view_create(ViewDesc* desc, ViewHandle* view);
        [DllImport(Dll)] public static extern Result weft_unity_view_destroy(ViewHandle view);
        [DllImport(Dll)] public static extern Result weft_unity_view_load(ViewHandle view, PageDesc* page);
        [DllImport(Dll)] public static extern Result weft_unity_view_resize(ViewHandle view, float logicalWidth, float logicalHeight, float deviceScale);
        [DllImport(Dll)] public static extern Result weft_unity_view_advance(ViewHandle view, double timeSeconds, uint* messages, uint* diagnostics);
        [DllImport(Dll)] public static extern Result weft_unity_view_message(ViewHandle view, uint index, Message* message);
        [DllImport(Dll)] public static extern Result weft_unity_view_diagnostic(ViewHandle view, uint index, uint* kind, byte** text, UIntPtr* size);
        [DllImport(Dll)] public static extern uint weft_unity_view_dropped_diagnostics(ViewHandle view);
        [DllImport(Dll)] public static extern Result weft_unity_view_set_storage_item(ViewHandle view, byte* key, UIntPtr keySize, byte* value, UIntPtr valueSize);
        [DllImport(Dll)] public static extern Result weft_unity_view_clear_storage(ViewHandle view);
        [DllImport(Dll)] public static extern Result weft_unity_view_get_storage(ViewHandle view, ulong revision, StorageItemFn fn, IntPtr user, ulong* outRevision);
        [DllImport(Dll)] public static extern Result weft_unity_view_evaluate(ViewHandle view, byte* source, UIntPtr sourceSize, TextFn fn, IntPtr user);
        [DllImport(Dll)] public static extern Result weft_unity_view_frame_info(ViewHandle view, ViewFrameInfo* info);
        [DllImport(Dll)] public static extern Result weft_unity_devtools_start(DevToolsDesc* desc);
        [DllImport(Dll)] public static extern Result weft_unity_devtools_stop();
        [DllImport(Dll)] public static extern Result weft_unity_devtools_get_info(DevToolsInfo* info);
        [DllImport(Dll)] public static extern Result weft_unity_view_pointer(ViewHandle view, PointerEvent* pointer, uint* prevented);
        [DllImport(Dll)] public static extern Result weft_unity_view_hit_test(ViewHandle view, float x, float y, Hit* hit);
        [DllImport(Dll)] public static extern Result weft_unity_view_key(ViewHandle view, KeyEvent* key, uint* handled);
        [DllImport(Dll)] public static extern Result weft_unity_view_text(ViewHandle view, byte* text, UIntPtr textSize, uint* handled);
        [DllImport(Dll)] public static extern Result weft_unity_view_set_focus(ViewHandle view, uint focused);
        [DllImport(Dll)] public static extern Result weft_unity_view_text_input(ViewHandle view, TextInput* input);
        [DllImport(Dll)] public static extern Result weft_unity_view_composition(ViewHandle view, byte* text, UIntPtr textSize, uint selectionStart, uint selectionEnd, uint* taken);
        [DllImport(Dll)] public static extern Result weft_unity_view_commit_text(ViewHandle view, byte* text, UIntPtr textSize, uint* taken);
        [DllImport(Dll)] public static extern Result weft_unity_view_gamepads(ViewHandle view, GamepadState* states, uint count, uint* handled);
        [DllImport(Dll)] public static extern Result weft_unity_set_texture(byte* name, UIntPtr nameSize, IntPtr texture, uint width, uint height);
        [DllImport(Dll)] public static extern uint weft_unity_queue_draw(ViewHandle view, IntPtr target, uint width, uint height, float scale, uint clear);
        [DllImport(Dll)] public static extern IntPtr weft_unity_render_event();
        [DllImport(Dll)] public static extern int weft_unity_render_event_id();
        [DllImport(Dll)] public static extern void weft_unity_render_get_stats(RenderStats* stats);
        [DllImport(Dll)] public static extern void weft_unity_render_set_measuring(uint enabled);
        [DllImport(Dll)] public static extern uint weft_unity_render_take_draw_times(DrawTime* outTimes, uint capacity);

        // weft's own, callable from any thread.
        [DllImport(Dll)] public static extern Result weft_get_build_info(BuildInfo* info);
        [DllImport(Dll)] public static extern Result weft_view_emit(IntPtr runtime, ViewHandle view, byte* name, UIntPtr nameSize, uint format, byte* payload, UIntPtr payloadSize, uint flags);
        [DllImport(Dll)] public static extern Result weft_view_complete_call(IntPtr runtime, ViewHandle view, ulong callId, uint succeeded, uint format, byte* payload, UIntPtr payloadSize);
        [DllImport(Dll)] public static extern Result weft_view_set_model(IntPtr runtime, ViewHandle view, byte* model, UIntPtr modelSize, uint format, byte* fields, UIntPtr fieldsSize);
        [DllImport(Dll)] public static extern Result weft_view_write_anchors(IntPtr runtime, ViewHandle view, WeftAnchor* anchors, UIntPtr count);
        [DllImport(Dll)] public static extern Result weft_keyboard_key(byte* code, UIntPtr codeSize, uint modifiers, byte* key);
        [DllImport(Dll)] public static extern IntPtr weft_result_string(Result result);

        public static string Text(byte* bytes, UIntPtr size)
        {
            return bytes == null ? string.Empty : System.Text.Encoding.UTF8.GetString(bytes, checked((int)size.ToUInt64()));
        }

        public static string Describe(Result result)
        {
            return Marshal.PtrToStringAnsi(weft_result_string(result)) ?? result.ToString();
        }
    }
}
