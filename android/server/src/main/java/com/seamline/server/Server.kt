package com.seamline.server

import android.net.LocalServerSocket
import android.os.Build
import android.os.Looper
import android.view.KeyEvent
import android.view.MotionEvent
import org.json.JSONObject
import kotlin.concurrent.thread
import kotlin.system.exitProcess

/**
 * Ponto de entrada, chamado pelo PC com:
 *   CLASSPATH=/data/local/tmp/seamline-server.apk app_process / com.seamline.server.Server <versão> [tamanho máximo]
 *
 * Roda como usuário "shell" (não como um app comum), o que permite capturar a tela,
 * criar telas virtuais e injetar toques.
 */
object Server {
    const val PROTOCOL_VERSION = 3
    private const val SOCKET_NAME = "seamline"
    private const val DEFAULT_MAX_SIZE = 1600

    @JvmStatic
    fun main(args: Array<String>) {
        val clientVersion = args.getOrNull(0)?.toIntOrNull()
        if (clientVersion != PROTOCOL_VERSION) {
            println("Versão do protocolo diferente: PC $clientVersion, celular $PROTOCOL_VERSION")
            exitProcess(1)
        }
        val maxSize = args.getOrNull(1)?.toIntOrNull() ?: DEFAULT_MAX_SIZE

        // Algumas APIs do sistema esperam um Looper na thread principal.
        Looper.prepareMainLooper()

        println("Seamline server ${BuildConfig.VERSION_NAME} aguardando o PC...")
        // O PC chega aqui pelo túnel "adb forward tcp:<porta> localabstract:seamline".
        // Primeira conexão: controle (mensagens JSON). Segunda: vídeo.
        val (control, video) = LocalServerSocket(SOCKET_NAME).use { server ->
            val control = server.accept()
            MessageChannel(control.inputStream, control.outputStream).send(hello())
            control to server.accept()
        }
        println("PC conectado.")

        thread(name = "video", isDaemon = true) {
            try {
                ScreenEncoder(maxSize).stream(video.outputStream)
            } catch (e: Exception) {
                println("Erro no vídeo: $e")
                exitProcess(2)
            }
        }

        control.use {
            val channel = MessageChannel(it.inputStream, it.outputStream)
            val bridge = PointerBridge { position ->
                channel.send(JSONObject().put("type", "pointer_leave").put("position", position.toDouble()))
            }
            bridge.use { serve(channel, InputInjector(), bridge) }
        }
        println("PC desconectou.")
        // app_process pode deixar threads do sistema vivas; saímos explicitamente.
        exitProcess(0)
    }

    private fun hello(): JSONObject {
        val screen = Displays.info()
        return JSONObject()
            .put("type", "hello")
            .put("protocol", PROTOCOL_VERSION)
            .put("manufacturer", Build.MANUFACTURER)
            .put("model", Build.MODEL)
            .put("android", Build.VERSION.RELEASE)
            .put("sdk", Build.VERSION.SDK_INT)
            .put("screenWidth", screen.width)
            .put("screenHeight", screen.height)
    }

    private fun serve(channel: MessageChannel, input: InputInjector, bridge: PointerBridge) {
        while (true) {
            val message = channel.receive() ?: return
            try {
                if (!handle(message, channel, input, bridge)) return
            } catch (e: Exception) {
                // Uma mensagem com problema não deve derrubar a conexão inteira.
                println("Erro ao processar ${message.optString("type")}: $e")
            }
        }
    }

    /** Retorna false quando o PC pediu para encerrar. */
    private fun handle(message: JSONObject, channel: MessageChannel, input: InputInjector, bridge: PointerBridge): Boolean {
        when (val type = message.optString("type")) {
            "ping" -> channel.send(JSONObject().put("type", "pong"))
            "touch" -> touchAction(message.optString("action"))?.let {
                input.touch(it, message.getDouble("x").toFloat(), message.getDouble("y").toFloat())
            }
            "scroll" -> input.scroll(
                message.getDouble("x").toFloat(), message.getDouble("y").toFloat(),
                message.optDouble("dx", 0.0).toFloat(), message.optDouble("dy", 0.0).toFloat(),
            )
            "key" -> keyCode(message.optString("key"))?.let(input::pressKey)
                ?: println("Tecla desconhecida: ${message.optString("key")}")
            "pointer_enter" -> bridge.enter(
                PointerBridge.Edge.valueOf(message.getString("edge").uppercase()),
                message.getDouble("position").toFloat(),
            )
            "pointer_exit" -> bridge.exit()
            "mouse" -> bridge.mouse(
                message.optInt("dx"), message.optInt("dy"), message.optInt("buttons"),
                message.optInt("wheel"), message.optInt("hwheel"),
            )
            "keyboard" -> bridge.setKeyboard(message.getBoolean("attached"))
            "keys" -> bridge.keys(message.optInt("modifiers"), message.optJSONArray("keys").toIntArray())
            "bye" -> return false
            else -> println("Mensagem desconhecida: $type")
        }
        return true
    }

    private fun org.json.JSONArray?.toIntArray() = IntArray(this?.length() ?: 0) { this!!.getInt(it) }

    private fun touchAction(name: String) = when (name) {
        "down" -> MotionEvent.ACTION_DOWN
        "move" -> MotionEvent.ACTION_MOVE
        "up" -> MotionEvent.ACTION_UP
        else -> null
    }

    private fun keyCode(name: String) = when (name) {
        "back" -> KeyEvent.KEYCODE_BACK
        "home" -> KeyEvent.KEYCODE_HOME
        "app_switch" -> KeyEvent.KEYCODE_APP_SWITCH
        "power" -> KeyEvent.KEYCODE_POWER
        else -> null
    }
}
