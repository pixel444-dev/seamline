package com.seamline.server

import android.os.SystemClock

/**
 * Fase 2: o mouse e o teclado do PC controlam o celular ("o celular vira uma segunda tela").
 *
 * O PC avisa quando o cursor atravessa a borda do monitor (pointer_enter) e passa a mandar o
 * movimento do mouse. Aqui ele vira um mouse UHID, então o Android mostra a setinha de verdade e
 * aplica a aceleração dele, como num mouse USB plugado no celular.
 * Quando a setinha encosta na borda combinada e o usuário continua empurrando, avisamos o PC
 * (pointer_leave) para o cursor voltar para o monitor.
 *
 * Se o botão esquerdo estiver apertado nessa hora (o usuário está "arrastando" a tela para fora),
 * o aviso vai com dragging = true: o PC leva junto o que está na frente (Fase 4).
 *
 * O teclado só existe enquanto o PC pede (keyboard attached): com um teclado físico conectado o
 * Android esconde o teclado da tela, e isso atrapalharia quem pega o celular na mão.
 */
class PointerBridge(private val onLeave: (position: Float, dragging: Boolean) -> Unit) : AutoCloseable {
    enum class Edge { LEFT, RIGHT, TOP, BOTTOM }

    private val mouse = UhidDevice.create("Seamline Mouse", Hid.MOUSE_DESCRIPTOR)
    private var keyboard: UhidDevice? = null

    /** Borda por onde o cursor entrou e vai sair; null quando o cursor está no PC. */
    private var edge: Edge? = null
    private var buttons = 0
    private var push = 0
    private var lastCheck = 0L

    /** O cursor do PC entrou no celular pela [edge], na posição [position] (0 a 1) ao longo dela. */
    fun enter(edge: Edge, position: Float) {
        this.edge = edge
        push = 0
        val screen = Displays.info()
        val along = position.coerceIn(0f, 1f)
        // Primeiro encosta na borda (o Android segura a setinha dentro da tela, então um empurrão
        // grande deixa ela exatamente na borda). Depois ajusta o outro eixo, conferindo a posição,
        // porque a aceleração do Android muda o tamanho de cada passo.
        val (pushX, pushY) = when (edge) {
            Edge.LEFT -> -MAX_DELTA to 0
            Edge.RIGHT -> MAX_DELTA to 0
            Edge.TOP -> 0 to -MAX_DELTA
            Edge.BOTTOM -> 0 to MAX_DELTA
        }
        move(pushX, pushY)
        val horizontal = edge == Edge.LEFT || edge == Edge.RIGHT
        val target = along * ((if (horizontal) screen.height else screen.width) - 1)
        repeat(4) {
            SystemClock.sleep(12)
            val p = InputService.cursorPosition() ?: return
            val error = target - (if (horizontal) p.y else p.x)
            if (kotlin.math.abs(error) < 3) return
            // Passos pequenos sofrem menos aceleração; dividir por 3 compensa a escala da tela.
            val step = (error / 3).toInt().coerceIn(-MAX_DELTA, MAX_DELTA)
            if (step == 0) return
            if (horizontal) move(0, step) else move(step, 0)
        }
    }

    /** O PC pegou o cursor de volta (atalho ou desconexão): solta os botões. */
    fun exit() {
        edge = null
        if (buttons != 0) {
            buttons = 0
            mouse.send(Hid.mouseReport(0, 0, 0, 0, 0))
        }
    }

    fun mouse(dx: Int, dy: Int, buttons: Int, wheel: Int, pan: Int) {
        this.buttons = buttons
        // Um relatório leva no máximo ±32767 por eixo e ±127 na roda; divide se passar.
        var restX = dx
        var restY = dy
        var restWheel = wheel
        var restPan = pan
        do {
            val x = restX.coerceIn(-MAX_DELTA, MAX_DELTA)
            val y = restY.coerceIn(-MAX_DELTA, MAX_DELTA)
            val w = restWheel.coerceIn(-127, 127)
            val p = restPan.coerceIn(-127, 127)
            mouse.send(Hid.mouseReport(buttons, x, y, w, p))
            restX -= x; restY -= y; restWheel -= w; restPan -= p
        } while (restX != 0 || restY != 0 || restWheel != 0 || restPan != 0)
        checkLeave(dx, dy)
    }

    fun setKeyboard(attached: Boolean) {
        if (attached && keyboard == null) {
            keyboard = UhidDevice.create("Seamline Keyboard", Hid.KEYBOARD_DESCRIPTOR)
        } else if (!attached) {
            keyboard?.let {
                it.send(Hid.keyboardReport(0, IntArray(0))) // solta tudo antes de remover
                it.close()
            }
            keyboard = null
        }
    }

    fun keys(modifiers: Int, keys: IntArray) {
        keyboard?.send(Hid.keyboardReport(modifiers, keys))
            ?: println("Teclas ignoradas: o teclado não está conectado")
    }

    /**
     * Se a setinha está na borda combinada e o usuário continua empurrando nessa direção, o cursor
     * volta para o PC. Ler a posição custa ~0,7 ms, então só olhamos quando o movimento vai na
     * direção da borda e no máximo a cada 15 ms.
     */
    private fun checkLeave(dx: Int, dy: Int) {
        val edge = edge ?: return
        val toward = when (edge) {
            Edge.LEFT -> -dx
            Edge.RIGHT -> dx
            Edge.TOP -> -dy
            Edge.BOTTOM -> dy
        }
        // Só o botão esquerdo apertado = arrastando; com outros botões o cursor não sai.
        if (toward <= 0 || (buttons != 0 && buttons != 1)) {
            push = 0
            return
        }
        push += toward
        val now = SystemClock.uptimeMillis()
        if (now - lastCheck < 15) return
        lastCheck = now

        val p = InputService.cursorPosition() ?: return
        val screen = Displays.info()
        val atEdge = when (edge) {
            Edge.LEFT -> p.x <= 0.5f
            Edge.RIGHT -> p.x >= screen.width - 1.5f
            Edge.TOP -> p.y <= 0.5f
            Edge.BOTTOM -> p.y >= screen.height - 1.5f
        }
        if (!atEdge) {
            push = 0
            return
        }
        val dragging = buttons == 1
        if (push < (if (dragging) DRAG_LEAVE_PUSH else LEAVE_PUSH)) return
        val position = when (edge) {
            Edge.LEFT, Edge.RIGHT -> p.y / (screen.height - 1)
            Edge.TOP, Edge.BOTTOM -> p.x / (screen.width - 1)
        }
        exit()
        onLeave(position.coerceIn(0f, 1f), dragging)
    }

    private fun move(dx: Int, dy: Int) = mouse.send(Hid.mouseReport(buttons, dx, dy, 0, 0))

    override fun close() {
        setKeyboard(false)
        mouse.close()
    }

    private companion object {
        const val MAX_DELTA = 32767

        /** Quanto (em unidades do mouse) é preciso empurrar contra a borda para voltar ao PC. */
        const val LEAVE_PUSH = 20

        /** Arrastando, pede um empurrão maior: não queremos levar o app por acidente ao rolar algo. */
        const val DRAG_LEAVE_PUSH = 60
    }
}
