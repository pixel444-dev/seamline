package com.seamline.server

import android.hardware.display.VirtualDisplay
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.view.Surface
import java.io.DataOutputStream
import java.io.OutputStream

/**
 * Captura a tela principal e manda em H.264 para o PC.
 *
 * Formato do fluxo (veja docs/protocolo.md):
 *   cabeçalho: largura (int32) + altura (int32)
 *   cada pacote: pts em µs (int64, bit 63 = configuração, bit 62 = quadro-chave) + tamanho (int32) + dados
 */
class ScreenEncoder(
    private val maxSize: Int,
    private val bitRate: Int = 8_000_000,
    private val maxFps: Int = 60,
) {
    fun stream(output: OutputStream) {
        val screen = Displays.info()
        val (codec, width, height) = createCodec(screen)
        val surface = codec.createInputSurface()
        var display: VirtualDisplay? = null
        try {
            display = Displays.createMirror("seamline", width, height, 0, surface) as VirtualDisplay
            codec.start()
            println("Vídeo: tela ${screen.width}x${screen.height} → ${width}x$height")

            val out = DataOutputStream(output.buffered(1 shl 16))
            out.writeInt(width)
            out.writeInt(height)
            out.flush()
            encodeLoop(codec, out)
        } finally {
            display?.release()
            codec.stop()
            codec.release()
            surface.release()
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

    /** Tenta tamanhos cada vez menores até o codificador do celular aceitar. */
    private fun createCodec(screen: Displays.Info): Triple<MediaCodec, Int, Int> {
        val sizes = listOf(maxSize, 1600, 1280, 1024, 800).filter { it <= maxSize }.distinct()
        var lastError: Exception? = null
        for (size in sizes) {
            val (width, height) = fit(screen.width, screen.height, size)
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

    private companion object {
        const val FLAG_CONFIG = 1L shl 63
        const val FLAG_KEY_FRAME = 1L shl 62

        /** Reduz para caber em [maxSize] mantendo a proporção; múltiplos de 16 evitam bordas no decodificador. */
        fun fit(width: Int, height: Int, maxSize: Int): Pair<Int, Int> {
            val scale = minOf(1.0, maxSize.toDouble() / maxOf(width, height))
            fun align(v: Double) = maxOf(16, (v / 16).toInt() * 16)
            return align(width * scale) to align(height * scale)
        }
    }
}
