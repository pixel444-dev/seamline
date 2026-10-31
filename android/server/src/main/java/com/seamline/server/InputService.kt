package com.seamline.server

import android.graphics.PointF
import android.os.IBinder

/**
 * Acesso direto ao serviço de entrada do Android (IInputManager), por reflexão.
 * Usado para saber onde está a setinha do mouse. O método existe a partir do Android 16;
 * em versões antigas a função só retorna null.
 */
object InputService {
    private val service: Any? by lazy {
        try {
            val binder = Class.forName("android.os.ServiceManager")
                .getMethod("getService", String::class.java).invoke(null, "input") as IBinder
            Class.forName("android.hardware.input.IInputManager\$Stub")
                .getMethod("asInterface", IBinder::class.java).invoke(null, binder)
        } catch (e: Exception) {
            println("Serviço de entrada indisponível: $e")
            null
        }
    }

    private val getCursorPosition by lazy {
        try {
            service?.javaClass?.getMethod("getCursorPositionInLogicalDisplay", Int::class.javaPrimitiveType)
        } catch (_: NoSuchMethodException) {
            println("Este Android não informa a posição do cursor: volte ao PC pelo atalho.")
            null
        }
    }

    /** Posição da setinha em pixels da tela (já considerando a rotação), ou null se não der para saber. */
    fun cursorPosition(displayId: Int = 0): PointF? {
        val method = getCursorPosition ?: return null
        val point = try {
            method.invoke(service, displayId) as PointF?
        } catch (e: Exception) {
            println("Não consegui ler a posição do cursor: ${e.cause ?: e}")
            null
        }
        return point?.takeUnless { it.x.isNaN() || it.y.isNaN() }
    }
}
