package com.seamline.server

import android.net.LocalSocket
import org.json.JSONObject
import java.io.DataInputStream
import java.io.OutputStream
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicInteger
import kotlin.concurrent.thread

/**
 * Fase 3 (F2): apps do celular em janelas do PC.
 *
 * Para cada janela, o server cria uma tela virtual do tamanho dela, move a tarefa do app para lá e
 * manda o vídeo dessa tela por uma conexão própria. O app continua rodando no celular; só passa a
 * desenhar numa tela que ninguém vê no celular. Quando a janela fecha, a tarefa volta para a tela
 * do celular (e, se o server cair, o próprio Android faz isso ao remover a tela virtual).
 *
 * Mensagens ao PC: app_opened, app_error e app_closed (veja docs/protocolo.md).
 */
class AppWindows(private val send: (JSONObject) -> Unit) : AutoCloseable {
    private val windows = ConcurrentHashMap<Int, AppWindow>()
    private val running = AtomicInteger()

    /** O PC pediu: o app da frente no celular vai para a janela [id], com esse tamanho em pixels. */
    fun open(id: Int, width: Int, height: Int, dpi: Int) {
        val task = Tasks.topApp(0)
        if (task == null) {
            send(message("app_error", id).put("message", "Não há um app aberto na tela do celular."))
            return
        }
        windows[id] = AppWindow(id, task, width, height, dpi)
        updatePower()
    }

    /** Mantém o celular acordado enquanto houver janela de app (veja [Power]). */
    private fun updatePower() {
        try {
            Power.keepAwake(windows.isNotEmpty())
        } catch (e: Exception) {
            println("Não consegui manter o celular acordado: ${e.cause ?: e}")
        }
    }

    /** Uma conexão nova de vídeo: o PC manda o id da janela (int32) logo ao conectar. */
    fun attach(socket: LocalSocket) = socket.use {
        val id = DataInputStream(it.inputStream).readInt()
        // A conexão pode chegar antes de o "app_open" (pela conexão de controle) ser processado.
        val deadline = System.currentTimeMillis() + 3000
        var window = windows[id]
        while (window == null && System.currentTimeMillis() < deadline) {
            Thread.sleep(20)
            window = windows[id]
        }
        if (window == null) {
            println("Conexão de vídeo para uma janela desconhecida: $id")
            return@use
        }
        window.run(it.outputStream)
    }

    fun resize(id: Int, width: Int, height: Int) = windows[id]?.resize(width, height)

    /** O PC fechou a janela: o app volta para a tela do celular. */
    fun close(id: Int) {
        windows.remove(id)?.close()
        updatePower()
    }

    /** Para onde vão os toques mandados para a janela [id]. */
    fun target(id: Int): InputInjector.Target? = windows[id]?.target

    /** Fecha todas e espera (até 1 s) os apps voltarem para o celular. */
    override fun close() {
        windows.keys.toList().forEach(::close)
        val deadline = System.currentTimeMillis() + 1000
        while (running.get() > 0 && System.currentTimeMillis() < deadline) Thread.sleep(10)
    }

    private fun message(type: String, id: Int) = JSONObject().put("type", type).put("id", id)

    private inner class AppWindow(
        val id: Int,
        val task: Tasks.Task,
        @Volatile var width: Int,
        @Volatile var height: Int,
        val dpi: Int,
    ) : ScreenEncoder.Source {
        private val encoder = ScreenEncoder(this)
        @Volatile private var display: VirtualDisplays.Handle? = null
        @Volatile private var closedByPc = false
        @Volatile private var error: String? = null
        @Volatile var target: InputInjector.Target? = null
            private set

        fun run(output: OutputStream) {
            if (closedByPc) return
            running.incrementAndGet()
            val watcher = thread(name = "app-$id", isDaemon = true) { watch() }
            try {
                encoder.stream(output)
            } catch (e: Exception) {
                if (display == null) error = e.cause?.message ?: e.message ?: e.toString()
                else if (!closedByPc) println("Vídeo da janela $id terminou: $e")
            } finally {
                watcher.interrupt()
                release()
                windows.remove(id, this)
                updatePower()
                when {
                    error != null -> send(message("app_error", id).put("message", error))
                    !closedByPc -> send(message("app_closed", id))
                }
                running.decrementAndGet()
            }
        }

        fun resize(width: Int, height: Int) {
            this.width = width
            this.height = height
            encoder.restart()
        }

        fun close() {
            closedByPc = true
            encoder.stop()
        }

        override fun sizes(): List<Pair<Int, Int>> {
            val w = width.coerceIn(MIN_SIZE, MAX_SIZE)
            val h = height.coerceIn(MIN_SIZE, MAX_SIZE)
            return listOf(maxOf(w, h), 1600, 1280, 1024).distinct().map { ScreenEncoder.fit(w, h, it) }
        }

        override fun attach(surface: android.view.Surface, width: Int, height: Int) {
            val current = display
            if (current != null) {
                VirtualDisplays.resize(current, width, height, dpi)
                VirtualDisplays.setSurface(current, surface)
            } else {
                val created = VirtualDisplays.create("Seamline $id", width, height, dpi, surface)
                display = created
                try {
                    Tasks.moveToDisplay(task.id, created.displayId)
                } catch (e: Exception) {
                    throw IllegalStateException("O Android não deixou mover o app para a janela: ${e.cause ?: e}", e)
                }
                val packageName = task.packageName!!
                send(
                    message("app_opened", id).put("package", packageName).put("label", Tasks.label(packageName)),
                )
                println("App $packageName (tarefa ${task.id}) foi para a tela ${created.displayId}")
            }
            target = InputInjector.Target(display!!.displayId, width, height)
        }

        override fun detach() {
            display?.let { VirtualDisplays.setSurface(it, null) }
        }

        /** Se o app fechar ou voltar sozinho para o celular (ex.: aberto pelo ícone), a janela fecha. */
        private fun watch() {
            try {
                while (true) {
                    Thread.sleep(1000)
                    val d = display ?: continue
                    if (Tasks.list(d.displayId, 1).isEmpty()) {
                        println("A janela $id ficou vazia")
                        encoder.stop()
                        return
                    }
                }
            } catch (_: InterruptedException) {
            }
        }

        private fun release() {
            val d = display ?: return
            display = null
            target = null
            try {
                // Devolve explicitamente, para o app aparecer por cima na tela do celular.
                for (t in Tasks.list(d.displayId)) Tasks.moveToDisplay(t.id, 0)
            } catch (e: Exception) {
                println("Não consegui devolver o app ao celular: ${e.cause ?: e}")
            }
            VirtualDisplays.release(d)
        }
    }

    private companion object {
        const val MIN_SIZE = 160
        const val MAX_SIZE = 4096
    }
}
