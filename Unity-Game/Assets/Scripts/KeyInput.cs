using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// The few keyboard keys the game uses, working with either Unity input
/// backend (old Input Manager or the new Input System package).
/// </summary>
public static class KeyInput
{
    public enum K { R, Space, Enter, LeftBracket, RightBracket, Escape, Up, Down, Left, Right }

    public static bool Pressed(K k)
    {
#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        return kb != null && Map(kb, k).wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
        return Input.GetKeyDown(Map(k));
#else
        return false;
#endif
    }

    public static bool Held(K k)
    {
#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        return kb != null && Map(kb, k).isPressed;
#elif ENABLE_LEGACY_INPUT_MANAGER
        return Input.GetKey(Map(k));
#else
        return false;
#endif
    }

#if ENABLE_INPUT_SYSTEM
    private static UnityEngine.InputSystem.Controls.KeyControl Map(Keyboard kb, K k)
    {
        switch (k)
        {
            case K.R: return kb.rKey;
            case K.Space: return kb.spaceKey;
            case K.Enter: return kb.enterKey;
            case K.LeftBracket: return kb.leftBracketKey;
            case K.RightBracket: return kb.rightBracketKey;
            case K.Escape: return kb.escapeKey;
            case K.Up: return kb.upArrowKey;
            case K.Down: return kb.downArrowKey;
            case K.Left: return kb.leftArrowKey;
            default: return kb.rightArrowKey;
        }
    }
#elif ENABLE_LEGACY_INPUT_MANAGER
    private static KeyCode Map(K k)
    {
        switch (k)
        {
            case K.R: return KeyCode.R;
            case K.Space: return KeyCode.Space;
            case K.Enter: return KeyCode.Return;
            case K.LeftBracket: return KeyCode.LeftBracket;
            case K.RightBracket: return KeyCode.RightBracket;
            case K.Escape: return KeyCode.Escape;
            case K.Up: return KeyCode.UpArrow;
            case K.Down: return KeyCode.DownArrow;
            case K.Left: return KeyCode.LeftArrow;
            default: return KeyCode.RightArrow;
        }
    }
#endif
}
