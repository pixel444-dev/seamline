using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using static Seamline.Desktop.Input.Native;

namespace Seamline.Desktop.Input;

/// <summary>
/// Fase 2: o cursor do PC atravessa a borda esquerda do monitor e passa a controlar o celular.
///
/// No PC: quando o cursor encosta na borda esquerda (e não há outro monitor daquele lado), ele
/// some e fica parado no meio do monitor. A partir daí o movimento do mouse, os cliques, a roda e o
/// teclado são "segurados" (não chegam ao Windows) e vão para o celular.
/// No celular: o server cria um mouse e um teclado virtuais (veja PointerBridge.kt). Quando a setinha
/// encosta na borda direita do celular e o usuário continua empurrando, o celular manda
/// "pointer_leave" e o cursor reaparece na borda esquerda do monitor, na mesma altura.
///
/// Saída de emergência: Ctrl + Alt + Esc traz o cursor de volta na hora.
/// </summary>
public sealed class CursorBridge : IDisposable
{
    /// <summary>Borda do celular por onde o cursor entra e sai (o celular fica à esquerda do monitor).</summary>
    private const string PhoneEdge = "right";
    private static readonly TimeSpan Cooldown = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan RawInputTimeout = TimeSpan.FromSeconds(1);

    private readonly DeviceSession _session;
    private readonly Action<string> _log;
    private readonly Dispatcher _ui;
    private readonly CursorShield _shield;
    private readonly InputQueue _queue;
    private readonly LowLevelInput _input;

    private readonly Lock _lock = new();
    private bool _onPhone;
    private RECT _monitor;
    private POINT _park;
    private DateTime _lastReturn;
    private DateTime _lastRawMove;
    private int _buttons;
    private int _wheel;
    private int _hwheel;
    private readonly HidKeyboardState _keys = new();

    /// <summary>Precisa ser criado na thread da interface (por causa da janela que esconde o cursor).</summary>
    public CursorBridge(DeviceSession session, Action<string> log)
    {
        _session = session;
        _log = log;
        _ui = Dispatcher.CurrentDispatcher;
        _shield = new CursorShield();
        _queue = new InputQueue(session, OnSendFailed);
        _session.PointerLeft += OnPointerLeft;
        _input = new LowLevelInput(OnMouse, OnKeyboard, OnRawMove);
    }

    public bool OnPhone
    {
        get { lock (_lock) return _onPhone; }
    }

    private bool OnMouse(int message, in MSLLHOOKSTRUCT data)
    {
        lock (_lock)
        {
            if (!_onPhone)
            {
                if (message == WM_MOUSEMOVE && ShouldEnter(data.pt))
                {
                    Enter(data.pt);
                    return true;
                }
                return false;
            }

            switch (message)
            {
                case WM_MOUSEMOVE:
                    // Normalmente o movimento vem do Raw Input; o gancho só serve de reserva.
                    if (DateTime.UtcNow - _lastRawMove > RawInputTimeout)
                        _queue.Move(data.pt.X - _park.X, data.pt.Y - _park.Y);
                    break;
                case WM_LBUTTONDOWN: SetButton(1, true); break;
                case WM_LBUTTONUP: SetButton(1, false); break;
                case WM_RBUTTONDOWN: SetButton(2, true); break;
                case WM_RBUTTONUP: SetButton(2, false); break;
                case WM_MBUTTONDOWN: SetButton(4, true); break;
                case WM_MBUTTONUP: SetButton(4, false); break;
                case WM_XBUTTONDOWN or WM_XBUTTONUP:
                    // XBUTTON1 = lateral de trás (Voltar), XBUTTON2 = lateral da frente (Avançar).
                    var bit = (data.mouseData >> 16) == 1 ? 8 : 16;
                    SetButton(bit, message == WM_XBUTTONDOWN);
                    break;
                case WM_MOUSEWHEEL:
                    _wheel += (short)(data.mouseData >> 16);
                    FlushWheel();
                    break;
                case WM_MOUSEHWHEEL:
                    _hwheel += (short)(data.mouseData >> 16);
                    FlushWheel();
                    break;
            }
            return true; // nada disso chega ao Windows
        }
    }

    private void OnRawMove(int dx, int dy)
    {
        lock (_lock)
        {
            _lastRawMove = DateTime.UtcNow;
            if (_onPhone)
                _queue.Move(dx, dy);
        }
    }

    private bool OnKeyboard(in KBDLLHOOKSTRUCT data)
    {
        lock (_lock)
        {
            if (!_onPhone)
                return false;
            // Com AltGr o Windows inventa um Ctrl esquerdo (scancode 0x21D): não é uma tecla de verdade.
            if ((data.scanCode & 0x200) != 0)
                return true;

            var up = (data.flags & LLKHF_UP) != 0;
            var hid = HidKeys.FromScanCode((int)data.scanCode, (data.flags & LLKHF_EXTENDED) != 0, (int)data.vkCode);
            if (hid == 0)
                return false; // teclas de volume/mídia continuam controlando o PC

            if (!up && hid == 0x29 && _keys.CtrlDown && _keys.AltDown)
            {
                ReturnToPc(null);
                return true;
            }
            if (up && !_keys.IsPressed(hid))
                return false; // tecla apertada antes de o cursor ir para o celular: o Windows precisa ver ela soltar
            if (up ? _keys.Release(hid) : _keys.Press(hid))
                _queue.Send(_keys.ToMessage());
            return true;
        }
    }

    /// <summary>O cursor encostou na borda esquerda do monitor mais à esquerda, sem botão apertado?</summary>
    private bool ShouldEnter(POINT pt)
    {
        if (DateTime.UtcNow - _lastReturn < Cooldown)
            return false;
        var monitor = MonitorRect(MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST));
        if (pt.X > monitor.Left)
            return false;
        var y = Math.Clamp(pt.Y, monitor.Top, monitor.Bottom - 1);
        if (MonitorFromPoint(new POINT(monitor.Left - 1, y), MONITOR_DEFAULTTONULL) != 0)
            return false; // tem outro monitor à esquerda: o cursor só passa para ele
        // Arrastando uma janela ou selecionando texto: não troca.
        return !IsDown(VK_LBUTTON) && !IsDown(VK_RBUTTON) && !IsDown(VK_MBUTTON);
    }

    private void Enter(POINT pt)
    {
        var monitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        _monitor = MonitorRect(monitor);
        _park = new POINT(_monitor.Left + _monitor.Width / 2, _monitor.Top + _monitor.Height / 2);
        _onPhone = true;
        _buttons = _wheel = _hwheel = 0;
        _keys.Clear();
        SetCursorPos(_park.X, _park.Y);
        var park = _park;
        _ui.BeginInvoke(() => { if (OnPhone) _shield.ShowAt(park.X, park.Y); });

        var position = Math.Clamp((pt.Y - _monitor.Top) / (double)(_monitor.Height - 1), 0, 1);
        _session.SetKeyboardActive(this, true);
        _queue.Send(new JsonObject { ["type"] = "pointer_enter", ["edge"] = PhoneEdge, ["position"] = position });
    }

    /// <summary>
    /// Traz o cursor de volta ao monitor. <paramref name="position"/> é a altura (0 a 1) em que ele
    /// saiu do celular; null quando foi o PC que pediu (atalho, erro ou desconexão).
    /// </summary>
    private void ReturnToPc(double? position)
    {
        lock (_lock)
        {
            if (!_onPhone)
                return;
            _onPhone = false;
            _lastReturn = DateTime.UtcNow;
            if (!_keys.IsEmpty)
            {
                _keys.Clear();
                _queue.Send(_keys.ToMessage());
            }
            if (position is null)
                _queue.Send(new JsonObject { ["type"] = "pointer_exit" });
            _session.SetKeyboardActive(this, false);
            _buttons = 0;

            var y = position is { } p ? _monitor.Top + (int)Math.Round(p * (_monitor.Height - 1)) : _park.Y;
            var x = position is null ? _park.X : _monitor.Left + 2;
            SetCursorPos(x, y);
            _ui.BeginInvoke(() => { if (!OnPhone) _shield.Hide(); });
        }
    }

    private void OnPointerLeft(double position) => ReturnToPc(position);

    private void OnSendFailed(Exception e)
    {
        _ui.BeginInvoke(() => _log($"Falha ao mandar o mouse para o celular: {e.Message}"));
        ReturnToPc(null);
    }

    private void SetButton(int bit, bool down)
    {
        var buttons = down ? _buttons | bit : _buttons & ~bit;
        if (buttons == _buttons)
            return;
        _buttons = buttons;
        _queue.Buttons(buttons);
    }

    /// <summary>A roda do Windows conta 120 por "clique"; mouses de rolagem suave mandam pedaços menores.</summary>
    private void FlushWheel()
    {
        var wheel = _wheel / 120;
        var hwheel = _hwheel / 120;
        if (wheel == 0 && hwheel == 0)
            return;
        _wheel -= wheel * 120;
        _hwheel -= hwheel * 120;
        _queue.Wheel(wheel, hwheel);
    }

    public void Dispose()
    {
        _session.PointerLeft -= OnPointerLeft;
        _input.Dispose();
        ReturnToPc(null);
        _queue.Dispose();
        _shield.Close();
    }

    /// <summary>
    /// Fila de envio. Os ganchos não podem esperar a rede, então só colocam eventos aqui e uma tarefa
    /// manda em ordem. Movimentos seguidos são somados num só, para não acumular atraso.
    /// </summary>
    private sealed class InputQueue : IDisposable
    {
        private sealed class MouseEvent
        {
            public int Dx, Dy, Buttons, Wheel, HWheel;
        }

        private readonly DeviceSession _session;
        private readonly Action<Exception> _onError;
        private readonly Lock _lock = new();
        private readonly List<object> _pending = [];
        private readonly SemaphoreSlim _signal = new(0);
        private readonly CancellationTokenSource _stop = new();
        private int _buttons;

        public InputQueue(DeviceSession session, Action<Exception> onError)
        {
            _session = session;
            _onError = onError;
            _ = Task.Run(SendLoop);
        }

        public void Move(int dx, int dy)
        {
            if (dx == 0 && dy == 0)
                return;
            lock (_lock)
            {
                // Só junta com outro movimento puro: um clique tem que acontecer onde ele foi feito.
                if (_pending.Count > 0 && _pending[^1] is MouseEvent { Wheel: 0, HWheel: 0 } last
                    && (last.Dx != 0 || last.Dy != 0))
                {
                    last.Dx += dx;
                    last.Dy += dy;
                    return;
                }
                Add(new MouseEvent { Dx = dx, Dy = dy, Buttons = _buttons });
            }
        }

        public void Buttons(int buttons)
        {
            lock (_lock)
            {
                _buttons = buttons;
                Add(new MouseEvent { Buttons = buttons });
            }
        }

        public void Wheel(int wheel, int hwheel)
        {
            lock (_lock)
                Add(new MouseEvent { Buttons = _buttons, Wheel = wheel, HWheel = hwheel });
        }

        public void Send(JsonObject message)
        {
            lock (_lock)
            {
                if (message["type"]?.GetValue<string>() == "pointer_exit")
                    _buttons = 0;
                Add(message);
            }
        }

        private void Add(object item)
        {
            _pending.Add(item);
            _signal.Release();
        }

        private async Task SendLoop()
        {
            var batch = new List<object>();
            try
            {
                while (true)
                {
                    await _signal.WaitAsync(_stop.Token);
                    lock (_lock)
                    {
                        batch.AddRange(_pending);
                        _pending.Clear();
                    }
                    foreach (var item in batch)
                        await _session.SendAsync(item as JsonObject ?? ToMessage((MouseEvent)item), _stop.Token);
                    batch.Clear();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                _onError(e);
            }
        }

        private static JsonObject ToMessage(MouseEvent e) => new()
        {
            ["type"] = "mouse",
            ["dx"] = e.Dx,
            ["dy"] = e.Dy,
            ["buttons"] = e.Buttons,
            ["wheel"] = e.Wheel,
            ["hwheel"] = e.HWheel,
        };

        public void Dispose()
        {
            // Dá um instante para as últimas mensagens (pointer_exit, soltar teclas) saírem.
            var deadline = DateTime.UtcNow.AddMilliseconds(300);
            while (DateTime.UtcNow < deadline)
            {
                lock (_lock)
                    if (_pending.Count == 0)
                        break;
                Thread.Sleep(10);
            }
            _stop.Cancel();
        }
    }
}
