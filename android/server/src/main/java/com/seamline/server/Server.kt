package com.seamline.server

import android.net.LocalServerSocket
import android.os.Build
import android.os.Looper
import android.view.KeyEvent
import android.view.MotionEvent
import org.json.JSONObject
import java.io.IOException
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
    const val PROTOCOL_VERSION = 4
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
        // Primeira conexão: controle (mensagens JSON). Segunda: vídeo da tela do celular.
        // As seguintes: vídeo das janelas de apps (cada uma começa com o id da janela).
        val server = LocalServerSocket(SOCKET_NAME)
        val control = server.accept()
        val channel = MessageChannel(control.inputStream, control.outputStream)
        channel.send(hello())
        val video = server.accept()
        println("PC conectado.")

        thread(name = "video", isDaemon = true) {
            try {
                ScreenEncoder(MirrorSource(maxSize)).stream(video.outputStream)
            } catch (e: Exception) {
                println("Erro no vídeo: $e")
                exitProcess(2)
            }
        }

        val send = { message: JSONObject ->
            try {
                channel.send(message)
            } catch (_: IOException) {
                // O PC desconectou; o loop de mensagens percebe e encerra.
            }
        }
        val apps = AppWindows(send)
        thread(name = "accept", isDaemon = true) {
            while (true) {
                val socket = try {
                    server.accept()
                } catch (_: IOException) {
                    break
                }
                thread(name = "app-video", isDaemon = true) {
                    try {
                        apps.attach(socket)
                    } catch (e: Exception) {
                        println("Erro na conexão de vídeo de uma janela: $e")
                    }
                }
            }
        }

        control.use {
            val bridge = PointerBridge { position, dragging ->
                val message = JSONObject().put("type", "pointer_leave").put("position", position.toDouble())
                if (dragging) dragKind()?.let { message.put("drag", it) }
                send(message)
            }
            bridge.use { apps.use { serve(channel, InputInjector(), bridge, apps) } }
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

    /**
     * O usuário arrastou a tela do celular para fora pela borda: o que vai junto para o PC?
     * "app" = o app da frente; "pc_window" = uma janela do PC que estava no celular (Fase 5).
     */
    private fun dragKind(): String? {
        val top = try {
            Tasks.topApp(0)
        } catch (e: Exception) {
            println("Não consegui ver o app da frente: ${e.cause ?: e}")
            null
        } ?: return null
        val isPcWindow = top.packageName == "com.seamline.app" && top.className?.endsWith(".RemoteWindowActivity") == true
        return if (isPcWindow) "pc_window" else "app"
    }

    private fun serve(channel: MessageChannel, input: InputInjector, bridge: PointerBridge, apps: AppWindows) {
        while (true) {
            val message = channel.receive() ?: return
            try {
                if (!handle(message, channel, input, bridge, apps)) return
            } catch (e: Exception) {
                // Uma mensagem com problema não deve derrubar a conexão inteira.
                println("Erro ao processar ${message.optString("type")}: $e")
            }
        }
    }

    /** Retorna false quando o PC pediu para encerrar. */
    private fun handle(
        message: JSONObject, channel: MessageChannel, input: InputInjector, bridge: PointerBridge, apps: AppWindows,
    ): Boolean {
        // Toques, rolagem e teclas com "window" vão para a janela de app com esse id.
        val window = if (message.has("window")) message.getInt("window") else null
        val target = window?.let { apps.target(it) ?: return true } // janela fechando: ignora
        if (target != null && !Power.isAwake()) Power.wakeUp() // a tela da janela dorme com o celular
        when (val type = message.optString("type")) {
            "ping" -> channel.send(JSONObject().put("type", "pong"))
            "touch" -> touchAction(message.optString("action"))?.let {
                input.touch(it, message.getDouble("x").toFloat(), message.getDouble("y").toFloat(), target)
            }
            "scroll" -> input.scroll(
                message.getDouble("x").toFloat(), message.getDouble("y").toFloat(),
                message.optDouble("dx", 0.0).toFloat(), message.optDouble("dy", 0.0).toFloat(), target,
            )
            "key" -> keyCode(message.optString("key"))?.let { input.pressKey(it, target?.displayId ?: 0) }
                ?: println("Tecla desconhecida: ${message.optString("key")}")
            "app_open" -> apps.open(
                message.getInt("id"), message.getInt("width"), message.getInt("height"), message.getInt("dpi"),
            )
            "app_resize" -> apps.resize(message.getInt("id"), message.getInt("width"), message.getInt("height"))
            "app_close" -> apps.close(message.getInt("id"))
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
