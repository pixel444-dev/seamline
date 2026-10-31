package com.seamline.server

import android.hardware.input.InputManager
import android.os.SystemClock
import android.view.InputDevice
import android.view.InputEvent
import android.view.KeyCharacterMap
import android.view.KeyEvent
import android.view.MotionEvent

/**
 * Injeta toques e teclas no Android como se viessem da tela/teclado de verdade.
 * Usa o InputManager oculto (InputManagerGlobal a partir do Android 14), como o scrcpy.
 * As coordenadas chegam normalizadas (0 a 1) e são convertidas para pixels da tela de destino:
 * a do celular ou a tela virtual de um app aberto numa janela do PC.
 */
class InputInjector {
    /** Tela que recebe o evento e o tamanho dela em pixels. */
    data class Target(val displayId: Int, val width: Int, val height: Int)

    private val manager: Any
    private val injectMethod: java.lang.reflect.Method
    private val setDisplayId = InputEvent::class.java.getMethod("setDisplayId", Int::class.javaPrimitiveType)
    private var downTime = 0L
    private var touchTarget = phone()

    init {
        val cls = try {
            Class.forName("android.hardware.input.InputManagerGlobal")
        } catch (_: ClassNotFoundException) {
            InputManager::class.java
        }
        manager = cls.getDeclaredMethod("getInstance").invoke(null)!!
        injectMethod = cls.getMethod("injectInputEvent", InputEvent::class.java, Int::class.javaPrimitiveType)
    }

    /** [target] null = a tela do celular. */
    fun touch(action: Int, x: Float, y: Float, target: Target? = null) {
        val now = SystemClock.uptimeMillis()
        if (action == MotionEvent.ACTION_DOWN) {
            downTime = now
            // A tela pode ter girado desde o último toque; o resto do gesto fica na mesma tela.
            touchTarget = target ?: phone()
        }
        val props = MotionEvent.PointerProperties().apply {
            id = 0
            toolType = MotionEvent.TOOL_TYPE_FINGER
        }
        val coords = pointer(x, y, touchTarget).apply { pressure = if (action == MotionEvent.ACTION_UP) 0f else 1f }
        val event = MotionEvent.obtain(
            downTime, now, action, 1, arrayOf(props), arrayOf(coords),
            0, 0, 1f, 1f, 0, 0, InputDevice.SOURCE_TOUCHSCREEN, 0,
        )
        inject(event, touchTarget.displayId)
        event.recycle()
    }

    /** [dx]/[dy] em "cliques" da roda do mouse; dy positivo rola para cima. */
    fun scroll(x: Float, y: Float, dx: Float, dy: Float, target: Target? = null) {
        val screen = target ?: phone()
        val now = SystemClock.uptimeMillis()
        val props = MotionEvent.PointerProperties().apply {
            id = 0
            toolType = MotionEvent.TOOL_TYPE_MOUSE
        }
        val coords = pointer(x, y, screen).apply {
            setAxisValue(MotionEvent.AXIS_HSCROLL, dx)
            setAxisValue(MotionEvent.AXIS_VSCROLL, dy)
        }
        val event = MotionEvent.obtain(
            now, now, MotionEvent.ACTION_SCROLL, 1, arrayOf(props), arrayOf(coords),
            0, 0, 1f, 1f, 0, 0, InputDevice.SOURCE_MOUSE, 0,
        )
        inject(event, screen.displayId)
        event.recycle()
    }

    /** Aperta e solta uma tecla (ex.: KeyEvent.KEYCODE_BACK). */
    fun pressKey(keyCode: Int, displayId: Int = 0) {
        val now = SystemClock.uptimeMillis()
        for (action in intArrayOf(KeyEvent.ACTION_DOWN, KeyEvent.ACTION_UP)) {
            inject(
                KeyEvent(
                    now, now, action, keyCode, 0, 0, KeyCharacterMap.VIRTUAL_KEYBOARD, 0, 0,
                    InputDevice.SOURCE_KEYBOARD,
                ),
                displayId,
            )
        }
    }

    private fun phone() = Displays.info().let { Target(0, it.width, it.height) }

    private fun pointer(x: Float, y: Float, target: Target) = MotionEvent.PointerCoords().apply {
        this.x = x.coerceIn(0f, 1f) * (target.width - 1)
        this.y = y.coerceIn(0f, 1f) * (target.height - 1)
        size = 1f
    }

    private fun inject(event: InputEvent, displayId: Int) {
        if (displayId != 0) setDisplayId.invoke(event, displayId)
        // Modo 0 = assíncrono: não espera o app processar o evento.
        injectMethod.invoke(manager, event, 0)
    }
}
