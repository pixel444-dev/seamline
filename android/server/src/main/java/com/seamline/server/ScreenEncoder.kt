package com.seamline.server

import android.hardware.display.VirtualDisplay
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.view.Surface
import java.io.DataOutputStream
import java.io.OutputStream

/**
 * Codifica uma tela em H.264 e manda para o PC.
 *
 * Formato do fluxo (veja docs/protocolo.md):
 *   cabeçalho: largura (int32) + altura (int32)
 *   cada pacote: pts em µs (int64, bit 63 = configuração, bit 62 = quadro-chave, bit 61 = novo
 *   tamanho) + tamanho (int32) + dados
 *
 * A tela de onde vem a imagem é um [Source]: o espelho da tela do celular ou a tela virtual de um
 * app enviado ao PC. Quando o tamanho muda, [restart] recria o codificador e manda um pacote de
 * "novo tamanho" no meio do fluxo.
 */
class ScreenEncoder(
    private val source: Source,
    private val bitRate: Int = 8_000_000,
    private val maxFps: Int = 60,
) {
    interface Source {
        /** Tamanhos para tentar, do preferido ao menor (o codificador do celular pode recusar alguns). */
        fun sizes(): List<Pair<Int, Int>>

        /** Passa a desenhar na [surface] (a entrada do codificador), que tem esse tamanho. */
        fun attach(surface: Surface, width: Int, height: Int)

        /** Para de desenhar na surface atual (o codificador vai ser recriado ou encerrado). */
        fun detach()
    }

    private val lock = Any()
    private var codec: MediaCodec? = null
    private var restartPending = false
    @Volatile private var stopped = false

    /** Recria o codificador com o tamanho atual do [source] (ex.: a janela do PC mudou de tamanho). */
    fun restart() = synchronized(lock) {
        val current = codec
        if (current == null) {
            restartPending = true // está sendo criado: o pedido vale para o próximo
            return@synchronized
        }
        try {
            current.signalEndOfInputStream()
        } catch (_: IllegalStateException) {
            // Já estava encerrando.
        }
    }

    fun stop() {
        stopped = true
        restart()
    }

    fun stream(output: OutputStream) {
        val out = DataOutputStream(output.buffered(1 shl 16))
        var first = true
        while (!stopped) {
            val (codec, width, height) = createCodec()
            val surface = codec.createInputSurface()
            try {
                source.attach(surface, width, height)
                codec.start()
                if (first) {
                    out.writeInt(width)
                    out.writeInt(height)
                } else {
                    out.writeLong(FLAG_SIZE)
                    out.writeInt(8)
                    out.writeInt(width)
                    out.writeInt(height)
                }
                out.flush()
                first = false
                synchronized(lock) {
                    this.codec = codec
                    if (restartPending || stopped) codec.signalEndOfInputStream()
                    restartPending = false
                }
                encodeLoop(codec, out)
            } finally {
                synchronized(lock) { this.codec = null }
                source.detach()
                codec.stop()
                codec.release()
                surface.release()
            }
        }
    }

    private fun encodeLoop(codec: MediaCodec, out: DataOutputStream) {
        val info = MediaCodec.BufferInfo()
        while (true) {
            val index = codec.dequeueOutputBuffer(info, -1)
            if (index < 0) continue
            try {
                if (info.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0) return
                val buffer = codec.getOutputBuffer(index) ?: continue
                var header = info.presentationTimeUs
                if (info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG != 0) header = header or FLAG_CONFIG
                if (info.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME != 0) header = header or FLAG_KEY_FRAME
                val data = ByteArray(info.size)
                buffer.position(info.offset)
                buffer.get(data)
                out.writeLong(header)
                out.writeInt(data.size)
                out.write(data)
                out.flush()
            } finally {
                codec.releaseOutputBuffer(index, false)
            }
        }
    }

    /** Tenta os tamanhos do [source], do maior para o menor, até o codificador do celular aceitar. */
    private fun createCodec(): Triple<MediaCodec, Int, Int> {
        var lastError: Exception? = null
        for ((width, height) in source.sizes()) {
            val codec = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
            try {
                codec.configure(format(width, height), null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
                return Triple(codec, width, height)
            } catch (e: Exception) {
                println("O codificador recusou ${width}x$height: ${e.message}")
                codec.release()
                lastError = e
            }
        }
        throw IllegalStateException("Nenhum tamanho de vídeo foi aceito", lastError)
    }

    private fun format(width: Int, height: Int) =
        MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, width, height).apply {
            setInteger(MediaFormat.KEY_BIT_RATE, bitRate)
            setInteger(MediaFormat.KEY_FRAME_RATE, maxFps)
            setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
            setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 10)
            // Sem isso, a tela parada não gera quadros e o PC fica sem imagem nova.
            setLong(MediaFormat.KEY_REPEAT_PREVIOUS_FRAME_AFTER, 100_000)
            setFloat(MediaFormat.KEY_MAX_FPS_TO_ENCODER, maxFps.toFloat())
        }

    companion object {
        private const val FLAG_CONFIG = 1L shl 63
        private const val FLAG_KEY_FRAME = 1L shl 62
        private const val FLAG_SIZE = 1L shl 61

        /** Reduz para caber em [maxSize] mantendo a proporção; múltiplos de 16 evitam bordas no decodificador. */
        fun fit(width: Int, height: Int, maxSize: Int): Pair<Int, Int> {
            val scale = minOf(1.0, maxSize.toDouble() / maxOf(width, height))
            fun align(v: Double) = maxOf(16, (v / 16).toInt() * 16)
            return align(width * scale) to align(height * scale)
        }
    }
}

/** A tela inteira do celular, espelhada (Fase 1). */
class MirrorSource(private val maxSize: Int) : ScreenEncoder.Source {
    private var display: VirtualDisplay? = null

    override fun sizes(): List<Pair<Int, Int>> {
        val screen = Displays.info()
        return listOf(maxSize, 1600, 1280, 1024, 800).filter { it <= maxSize }.distinct()
            .map { ScreenEncoder.fit(screen.width, screen.height, it) }
    }

    override fun attach(surface: Surface, width: Int, height: Int) {
        val screen = Displays.info()
        display = Displays.createMirror("seamline", width, height, 0, surface) as VirtualDisplay
        println("Vídeo: tela ${screen.width}x${screen.height} → ${width}x$height")
    }

    override fun detach() {
        display?.release()
        display = null
    }
}
