package com.seamline.server

import android.net.LocalServerSocket
import android.os.Build
import org.json.JSONObject
import kotlin.system.exitProcess

/**
 * Ponto de entrada, chamado pelo PC com:
 *   CLASSPATH=/data/local/tmp/seamline-server.apk app_process / com.seamline.server.Server <versão>
 *
 * Roda como usuário "shell" (não como um app comum), o que vai permitir capturar a tela,
 * criar telas virtuais e injetar mouse/teclado nas próximas fases.
 */
object Server {
    const val PROTOCOL_VERSION = 1
    private const val SOCKET_NAME = "seamline"

    @JvmStatic
    fun main(args: Array<String>) {
        val clientVersion = args.firstOrNull()?.toIntOrNull()
        if (clientVersion != PROTOCOL_VERSION) {
            println("Versão do protocolo diferente: PC $clientVersion, celular $PROTOCOL_VERSION")
            exitProcess(1)
        }

        println("Seamline server ${BuildConfig.VERSION_NAME} aguardando o PC...")
        // O PC chega aqui pelo túnel "adb forward tcp:<porta> localabstract:seamline".
        val socket = LocalServerSocket(SOCKET_NAME).use { it.accept() }
        socket.use {
            val channel = MessageChannel(it.inputStream, it.outputStream)
            channel.send(hello())
            println("PC conectado.")
            serve(channel)
        }
        println("PC desconectou.")
        // app_process pode deixar threads do sistema vivas; saímos explicitamente.
        exitProcess(0)
    }

    private fun hello() = JSONObject()
        .put("type", "hello")
        .put("protocol", PROTOCOL_VERSION)
        .put("manufacturer", Build.MANUFACTURER)
        .put("model", Build.MODEL)
        .put("android", Build.VERSION.RELEASE)
        .put("sdk", Build.VERSION.SDK_INT)

    private fun serve(channel: MessageChannel) {
        while (true) {
            val message = channel.receive() ?: return
            when (val type = message.optString("type")) {
                "ping" -> channel.send(JSONObject().put("type", "pong"))
                "bye" -> return
                else -> println("Mensagem desconhecida: $type")
            }
        }
    }
}
