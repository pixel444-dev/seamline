using System.Text.Json.Nodes;

namespace Seamline.Desktop.Input;

/// <summary>
/// Converte as teclas do PC para os códigos HID que um teclado USB manda.
/// Usamos o "scancode" (a posição física da tecla), não a letra: quem decide a letra é o layout
/// configurado no celular (Configurações → Teclado físico → Português (Brasil) para o ABNT2).
/// Assim acentos, Ç e as teclas extras do ABNT2 funcionam igual a um teclado plugado no celular.
/// </summary>
public static class HidKeys
{
    public const int LeftControl = 0xE0;
    public const int RightGui = 0xE7;

    // Scancodes do "conjunto 1" (o que o Windows informa) → código de uso HID.
    private static readonly Dictionary<int, int> Normal = new()
    {
        [0x01] = 0x29, // Esc
        [0x02] = 0x1E, [0x03] = 0x1F, [0x04] = 0x20, [0x05] = 0x21, [0x06] = 0x22, // 1 a 5
        [0x07] = 0x23, [0x08] = 0x24, [0x09] = 0x25, [0x0A] = 0x26, [0x0B] = 0x27, // 6 a 0
        [0x0C] = 0x2D, [0x0D] = 0x2E, [0x0E] = 0x2A, [0x0F] = 0x2B, // - = Backspace Tab
        [0x10] = 0x14, [0x11] = 0x1A, [0x12] = 0x08, [0x13] = 0x15, [0x14] = 0x17, // Q W E R T
        [0x15] = 0x1C, [0x16] = 0x18, [0x17] = 0x0C, [0x18] = 0x12, [0x19] = 0x13, // Y U I O P
        [0x1A] = 0x2F, [0x1B] = 0x30, [0x1C] = 0x28, [0x1D] = 0xE0, // [ ] Enter Ctrl esquerdo
        [0x1E] = 0x04, [0x1F] = 0x16, [0x20] = 0x07, [0x21] = 0x09, [0x22] = 0x0A, // A S D F G
        [0x23] = 0x0B, [0x24] = 0x0D, [0x25] = 0x0E, [0x26] = 0x0F, // H J K L
        [0x27] = 0x33, [0x28] = 0x34, [0x29] = 0x35, [0x2A] = 0xE1, [0x2B] = 0x31, // ; ' ` Shift esq. \
        [0x2C] = 0x1D, [0x2D] = 0x1B, [0x2E] = 0x06, [0x2F] = 0x19, [0x30] = 0x05, // Z X C V B
        [0x31] = 0x11, [0x32] = 0x10, [0x33] = 0x36, [0x34] = 0x37, [0x35] = 0x38, // N M , . /
        [0x36] = 0xE5, [0x37] = 0x55, [0x38] = 0xE2, [0x39] = 0x2C, [0x3A] = 0x39, // Shift dir. * Alt Espaço Caps
        [0x3B] = 0x3A, [0x3C] = 0x3B, [0x3D] = 0x3C, [0x3E] = 0x3D, [0x3F] = 0x3E, // F1 a F5
        [0x40] = 0x3F, [0x41] = 0x40, [0x42] = 0x41, [0x43] = 0x42, [0x44] = 0x43, // F6 a F10
        [0x46] = 0x47, // Scroll Lock
        [0x47] = 0x5F, [0x48] = 0x60, [0x49] = 0x61, [0x4A] = 0x56, // teclado numérico 7 8 9 -
        [0x4B] = 0x5C, [0x4C] = 0x5D, [0x4D] = 0x5E, [0x4E] = 0x57, // 4 5 6 +
        [0x4F] = 0x59, [0x50] = 0x5A, [0x51] = 0x5B, [0x52] = 0x62, [0x53] = 0x63, // 1 2 3 0 ,
        [0x56] = 0x64, // tecla \ | ao lado do Shift esquerdo (ABNT2/ISO)
        [0x57] = 0x44, [0x58] = 0x45, // F11 F12
        [0x70] = 0x88, [0x73] = 0x87, // Katakana; tecla / ? ao lado do Shift direito (ABNT2)
        [0x79] = 0x8A, [0x7B] = 0x8B, [0x7D] = 0x89, // Henkan, Muhenkan, Iene
        [0x7E] = 0x85, // ponto do teclado numérico do ABNT2
    };

    // Teclas "estendidas" (o Windows marca com o prefixo E0).
    private static readonly Dictionary<int, int> Extended = new()
    {
        [0x1C] = 0x58, // Enter do teclado numérico
        [0x1D] = 0xE4, // Ctrl direito
        [0x35] = 0x54, // / do teclado numérico
        [0x37] = 0x46, // Print Screen
        [0x38] = 0xE6, // Alt direito (AltGr)
        [0x47] = 0x4A, [0x48] = 0x52, [0x49] = 0x4B, // Home ↑ Page Up
        [0x4B] = 0x50, [0x4D] = 0x4F, // ← →
        [0x4F] = 0x4D, [0x50] = 0x51, [0x51] = 0x4E, // End ↓ Page Down
        [0x52] = 0x49, [0x53] = 0x4C, // Insert Delete
        [0x5B] = 0xE3, [0x5C] = 0xE7, [0x5D] = 0x65, // Windows esquerdo/direito, Menu
    };

    /// <summary>
    /// Código HID da tecla, ou 0 se ela não existe num teclado comum (ex.: volume, mídia).
    /// Algumas teclas o Windows informa de um jeito torto, então olhamos o código virtual antes.
    /// </summary>
    public static int FromScanCode(int scanCode, bool extended, int virtualKey)
    {
        switch (virtualKey)
        {
            case 0x13: return 0x48; // Pause
            case 0x90: return 0x53; // Num Lock
            case 0x2C: return 0x46; // Print Screen
        }
        var table = extended ? Extended : Normal;
        return table.GetValueOrDefault(scanCode);
    }

    public static bool IsModifier(int hid) => hid is >= LeftControl and <= RightGui;
}

/// <summary>
/// Quais teclas estão apertadas, no formato do teclado HID: um byte de modificadores
/// (Ctrl, Shift, Alt, Win) e até 6 outras teclas ao mesmo tempo.
/// </summary>
public sealed class HidKeyboardState
{
    private readonly List<int> _keys = [];
    private int _modifiers;

    public bool IsEmpty => _modifiers == 0 && _keys.Count == 0;
    public bool CtrlDown => (_modifiers & 0x11) != 0;
    public bool AltDown => (_modifiers & 0x44) != 0;

    public bool IsPressed(int hid) =>
        HidKeys.IsModifier(hid) ? (_modifiers & ModifierBit(hid)) != 0 : _keys.Contains(hid);

    /// <summary>Retorna true se o estado mudou (a repetição automática do Windows não muda nada).</summary>
    public bool Press(int hid)
    {
        if (IsPressed(hid))
            return false;
        if (HidKeys.IsModifier(hid))
            _modifiers |= ModifierBit(hid);
        else if (_keys.Count < 6)
            _keys.Add(hid);
        else
            return false;
        return true;
    }

    public bool Release(int hid)
    {
        if (!IsPressed(hid))
            return false;
        if (HidKeys.IsModifier(hid))
            _modifiers &= ~ModifierBit(hid);
        else
            _keys.Remove(hid);
        return true;
    }

    public void Clear()
    {
        _keys.Clear();
        _modifiers = 0;
    }

    /// <summary>Mensagem "keys" do protocolo com o estado atual.</summary>
    public JsonObject ToMessage() => new()
    {
        ["type"] = "keys",
        ["modifiers"] = _modifiers,
        ["keys"] = new JsonArray(_keys.Select(k => JsonValue.Create(k)).ToArray<JsonNode?>()),
    };

    private static int ModifierBit(int hid) => 1 << (hid - HidKeys.LeftControl);
}
