package com.seamline.app

import android.app.Activity
import android.content.Intent
import android.content.pm.ActivityInfo
import android.content.res.Configuration
import android.graphics.Color
import android.media.MediaCodec
import android.media.MediaFormat
import android.os.Bundle
import android.util.Log
import android.view.Gravity
import android.view.InputDevice
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.View
import android.view.WindowInsets
import android.view.WindowInsetsController
import android.view.WindowManager
import android.widget.FrameLayout
import org.json.JSONObject
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.IOException
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.Executors
import kotlin.concurrent.thread

/**
 * Fase 5 (F3): mostra uma janela do PC na tela do celular.
 *
 * O PC abre esta tela com `am start ... --ei port <porta>` e cria um túnel "adb reverse", então a
 * porta local do celular chega no PC. Pela conexão:
 * - o PC manda o vídeo da janela (mesmo formato do vídeo do celular: tamanho + pacotes H.264);
 * - o celular manda toques, mouse e teclas (mensagens JSON com 4 bytes de tamanho na frente),
 *   que o PC repassa para a janela de verdade.
 *
 * Voltar (ou fechar esta tela) devolve a janela para o PC.
 */
class RemoteWindowActivity : Activity(), SurfaceHolder.Callback {
    private lateinit var surfaceView: SurfaceView
    private lateinit var container: FrameLayout

    private val lock = Any()
    private var connection: Connection? = null
    private var surfaceReady = false
    private var videoWidth = 0
    private var videoHeight = 0

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // O usuário mandou a janela agora, do PC: mostra mesmo com o celular bloqueado.
        setShowWhenLocked(true)
        setTurnScreenOn(true)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)

        surfaceView = SurfaceView(this)
        container = FrameLayout(this).apply {
            setBackgroundColor(Color.BLACK)
            addView(surfaceView, FrameLayout.LayoutParams(1, 1, Gravity.CENTER))
            addOnLayoutChangeListener { _, _, _, _, _, _, _, _, _ -> fitVideo() }
        }
        setContentView(container)
        window.insetsController?.let {
            it.hide(WindowInsets.Type.systemBars())
            it.systemBarsBehavior = WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        }

        surfaceView.holder.addCallback(this)
        surfaceView.setOnTouchListener { _, event -> onPointer(event) }
        surfaceView.setOnGenericMotionListener { _, event -> onPointer(event) }
        surfaceView.isFocusable = true
        surfaceView.isFocusableInTouchMode = true
        surfaceView.requestFocus()

        connect(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        connect(intent) // outra janela do PC: troca a conexão
    }

    override fun onConfigurationChanged(newConfig: Configuration) {
        super.onConfigurationChanged(newConfig)
        container.post { fitVideo() }
    }

    override fun onDestroy() {
        super.onDestroy()
        synchronized(lock) {
            connection?.close()
            connection = null
        }
    }

    private fun connect(intent: Intent) {
        val port = intent.getIntExtra("port", 0)
        if (port == 0) {
            finish()
            return
        }
        synchronized(lock) {
            connection?.close()
            connection = Connection(port).also { it.start() }
        }
    }

    // A surface (onde o decodificador desenha) some quando a tela fica escondida e volta depois.
    override fun surfaceCreated(holder: SurfaceHolder) {
        synchronized(lock) {
            surfaceReady = true
            connection?.surfaceChanged()
        }
    }

    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {}

    override fun surfaceDestroyed(holder: SurfaceHolder) {
        synchronized(lock) {
            surfaceReady = false
            connection?.surfaceChanged()
        }
    }

    /** Ajusta a SurfaceView ao tamanho do vídeo, mantendo a proporção (sobra preto nas bordas). */
    private fun fitVideo() {
        if (videoWidth == 0 || container.width == 0) return
        val scale = minOf(container.width.toFloat() / videoWidth, container.height.toFloat() / videoHeight)
        val params = surfaceView.layoutParams as FrameLayout.LayoutParams
        val w = (videoWidth * scale).toInt()
        val h = (videoHeight * scale).toInt()
        if (params.width != w || params.height != h) {
            params.width = w
            params.height = h
            surfaceView.layoutParams = params
        }
    }

    private fun onVideoSize(width: Int, height: Int) = runOnUiThread {
        videoWidth = width
        videoHeight = height
        // Janela larga: deita o celular para ela ficar maior.
        requestedOrientation = if (width > height * 1.2) {
            ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
        } else {
            ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED
        }
        fitVideo()
    }

    /** Toques e mouse viram cliques na janela do PC. */
    private fun onPointer(event: MotionEvent): Boolean {
        val x = (event.x / surfaceView.width).coerceIn(0f, 1f)
        val y = (event.y / surfaceView.height).coerceIn(0f, 1f)
        val isMouse = event.isFromSource(InputDevice.SOURCE_MOUSE)
        val message = when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> pointer("down", x, y)
            MotionEvent.ACTION_MOVE -> pointer("move", x, y)
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> pointer("up", x, y)
            MotionEvent.ACTION_HOVER_MOVE -> pointer("hover", x, y)
            MotionEvent.ACTION_SCROLL -> pointer("scroll", x, y)
                .put("dx", event.getAxisValue(MotionEvent.AXIS_HSCROLL).toDouble())
                .put("dy", event.getAxisValue(MotionEvent.AXIS_VSCROLL).toDouble())
            // Botão direito do mouse: vira clique direito no PC (e não "Voltar", como o Android faria).
            MotionEvent.ACTION_BUTTON_PRESS, MotionEvent.ACTION_BUTTON_RELEASE -> {
                if (event.actionButton != MotionEvent.BUTTON_SECONDARY) return isMouse
                val down = event.actionMasked == MotionEvent.ACTION_BUTTON_PRESS
                pointer(if (down) "right_down" else "right_up", x, y)
            }
            else -> return false
        }
        // Com o mouse, o botão esquerdo também chega como ACTION_DOWN/UP; outros botões são ignorados.
        if (isMouse && event.actionMasked == MotionEvent.ACTION_DOWN &&
            event.buttonState and MotionEvent.BUTTON_PRIMARY == 0
        ) {
            return true
        }
        send(message)
        return true
    }

    private fun pointer(action: String, x: Float, y: Float) =
        JSONObject().put("type", "pointer").put("action", action).put("x", x.toDouble()).put("y", y.toDouble())

    /** Teclas (do teclado do PC, quando o cursor está no celular) voltam para a janela no PC. */
    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        when (event.keyCode) {
            KeyEvent.KEYCODE_BACK, KeyEvent.KEYCODE_HOME, KeyEvent.KEYCODE_APP_SWITCH,
            KeyEvent.KEYCODE_VOLUME_UP, KeyEvent.KEYCODE_VOLUME_DOWN, KeyEvent.KEYCODE_POWER,
            -> return super.dispatchKeyEvent(event)
        }
        if (event.action == KeyEvent.ACTION_MULTIPLE) return true
        send(
            JSONObject().put("type", "key")
                .put("down", event.action == KeyEvent.ACTION_DOWN)
                .put("code", event.keyCode)
                .put("char", event.getUnicodeChar(event.metaState and (KeyEvent.META_CTRL_MASK or KeyEvent.META_ALT_MASK).inv()))
                .put("ctrl", event.isCtrlPressed)
                .put("alt", event.isAltPressed)
                .put("shift", event.isShiftPressed),
        )
        return true
    }

    private fun send(message: JSONObject) {
        synchronized(lock) { connection }?.send(message)
    }

    /** A conexão com o PC: lê o vídeo numa thread e decodifica direto na SurfaceView. */
    private inner class Connection(private val port: Int) {
        private val socket = Socket()
        @Volatile private var closed = false
        private var output: DataOutputStream? = null
        private var codec: MediaCodec? = null
        private val sender = Executors.newSingleThreadExecutor()

        fun start() = thread(name = "pc-window") { run() }

        fun close() {
            closed = true
            sender.shutdown()
            try {
                socket.close()
            } catch (_: IOException) {
            }
        }

        fun send(message: JSONObject) {
            val out = output ?: return
            val bytes = message.toString().toByteArray(Charsets.UTF_8)
            try {
                sender.execute {
                    try {
                        out.writeInt(bytes.size)
                        out.write(bytes)
                        out.flush()
                    } catch (_: IOException) {
                    }
                }
            } catch (_: java.util.concurrent.RejectedExecutionException) {
                // Conexão já fechada.
            }
        }

        /** A surface apareceu ou sumiu: recria o decodificador e pede um quadro completo ao PC. */
        fun surfaceChanged() = thread {
            synchronized(this) {
                releaseCodec()
                if (surfaceReady && videoWidth > 0) {
                    createCodec(videoWidth, videoHeight)
                    send(JSONObject().put("type", "keyframe"))
                }
            }
        }

        private fun run() {
            try {
                socket.tcpNoDelay = true
                socket.connect(InetSocketAddress("127.0.0.1", port), 5000)
                val input = DataInputStream(socket.getInputStream().buffered(1 shl 16))
                output = DataOutputStream(socket.getOutputStream().buffered())
                onSize(input.readInt(), input.readInt())
                while (!closed) {
                    val header = input.readLong()
                    val data = ByteArray(input.readInt())
                    input.readFully(data)
                    when {
                        header and FLAG_SIZE != 0L -> {
                            val size = DataInputStream(data.inputStream())
                            onSize(size.readInt(), size.readInt())
                        }
                        header and FLAG_CONFIG != 0L -> decode(data, header and PTS_MASK, MediaCodec.BUFFER_FLAG_CODEC_CONFIG)
                        else -> decode(data, header and PTS_MASK, 0)
                    }
                }
            } catch (e: IOException) {
                if (!closed) Log.i(TAG, "Conexão com o PC terminou: $e")
            } finally {
                synchronized(this) { releaseCodec() }
                // O PC fechou (a janela voltou para ele): esta tela não tem mais o que mostrar.
                if (!closed) runOnUiThread { if (synchronized(lock) { connection } === this) finish() }
            }
        }

        private fun onSize(width: Int, height: Int) {
            videoWidth = width
            videoHeight = height
            onVideoSize(width, height)
            synchronized(this) {
                releaseCodec()
                if (surfaceReady) createCodec(width, height)
            }
        }

        private fun createCodec(width: Int, height: Int) {
            val surface = surfaceView.holder.surface
            if (!surface.isValid) return
            codec = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC).apply {
                val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, width, height)
                format.setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
                configure(format, surface, null, 0)
                start()
            }
        }

        @Synchronized
        private fun decode(data: ByteArray, pts: Long, flags: Int) {
            val codec = codec ?: return
            try {
                val index = codec.dequeueInputBuffer(100_000)
                if (index >= 0) {
                    codec.getInputBuffer(index)!!.apply { clear(); put(data) }
                    codec.queueInputBuffer(index, 0, data.size, pts, flags)
                }
                val info = MediaCodec.BufferInfo()
                while (true) {
                    val out = codec.dequeueOutputBuffer(info, 0)
                    if (out >= 0) codec.releaseOutputBuffer(out, true) else if (out == MediaCodec.INFO_TRY_AGAIN_LATER) break
                }
            } catch (e: IllegalStateException) {
                Log.w(TAG, "Erro no decodificador: $e")
            }
        }

        private fun releaseCodec() {
            codec?.let {
                try {
                    it.stop()
                } catch (_: IllegalStateException) {
                }
                it.release()
            }
            codec = null
        }
    }

    private companion object {
        const val TAG = "Seamline"
        const val FLAG_CONFIG = 1L shl 63
        const val FLAG_SIZE = 1L shl 61
        const val PTS_MASK = (1L shl 61) - 1
    }
}
