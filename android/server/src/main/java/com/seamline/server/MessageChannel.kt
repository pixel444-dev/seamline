package com.seamline.server

import org.json.JSONObject
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.EOFException
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream

/**
 * Canal de controle: cada mensagem é um JSON precedido de 4 bytes com o tamanho (big-endian).
 * Veja docs/protocolo.md.
 */
class MessageChannel(input: InputStream, output: OutputStream) {
    private val input = DataInputStream(input.buffered())
    private val output = DataOutputStream(output.buffered())

    @Synchronized
    fun send(message: JSONObject) {
        val payload = message.toString().toByteArray(Charsets.UTF_8)
        output.writeInt(payload.size)
        output.write(payload)
        output.flush()
    }

    /** Lê a próxima mensagem. Retorna null quando o PC fecha a conexão. */
    fun receive(): JSONObject? {
        val length = try {
            input.readInt()
        } catch (_: EOFException) {
            return null
        }
        if (length !in 0..MAX_MESSAGE_SIZE) throw IOException("Tamanho de mensagem inválido: $length")
        val payload = ByteArray(length)
        input.readFully(payload)
        return JSONObject(String(payload, Charsets.UTF_8))
    }

    private companion object {
        const val MAX_MESSAGE_SIZE = 16 * 1024 * 1024
    }
}
