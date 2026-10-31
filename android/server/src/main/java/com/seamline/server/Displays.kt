package com.seamline.server

import android.view.Surface

/**
 * Acesso às telas do Android por APIs ocultas (só funcionam porque o server roda como "shell").
 * Os nomes de classes e métodos são os mesmos que o scrcpy usa.
 */
object Displays {
    data class Info(val width: Int, val height: Int, val rotation: Int)

    private val globalClass by lazy { Class.forName("android.hardware.display.DisplayManagerGlobal") }
    private val global by lazy { globalClass.getDeclaredMethod("getInstance").invoke(null) }
    private val getDisplayInfo by lazy { globalClass.getMethod("getDisplayInfo", Int::class.javaPrimitiveType) }

    /** Tamanho atual da tela em pixels (já considerando a rotação). */
    fun info(displayId: Int = 0): Info {
        val info = getDisplayInfo.invoke(global, displayId)
            ?: throw IllegalStateException("Tela $displayId não encontrada")
        val cls = info.javaClass
        return Info(
            width = cls.getField("logicalWidth").getInt(info),
            height = cls.getField("logicalHeight").getInt(info),
            rotation = cls.getField("rotation").getInt(info),
        )
    }

    /**
     * Cria uma tela virtual que espelha [displayIdToMirror] e desenha em [surface]
     * (a entrada do codificador de vídeo). Retorna um android.hardware.display.VirtualDisplay.
     */
    fun createMirror(name: String, width: Int, height: Int, displayIdToMirror: Int, surface: Surface): Any {
        val method = android.hardware.display.DisplayManager::class.java.getMethod(
            "createVirtualDisplay",
            String::class.java, Int::class.javaPrimitiveType, Int::class.javaPrimitiveType,
            Int::class.javaPrimitiveType, Surface::class.java,
        )
        return method.invoke(null, name, width, height, displayIdToMirror, surface)
            ?: throw IllegalStateException("Não foi possível criar a tela virtual")
    }
}
