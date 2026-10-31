package com.seamline.server

import android.hardware.display.VirtualDisplayConfig
import android.os.Binder
import android.os.IBinder
import android.os.Looper
import android.os.SystemClock
import android.view.Surface
import kotlin.concurrent.thread

/** Serviços internos do Android, pegos direto do ServiceManager (só funciona como "shell"). */
private fun systemService(name: String, stubClass: String): Any {
    val binder = Class.forName("android.os.ServiceManager")
        .getMethod("getService", String::class.java).invoke(null, name) as IBinder
    return Class.forName("$stubClass\$Stub").getMethod("asInterface", IBinder::class.java).invoke(null, binder)!!
}

private fun Any.method(name: String) = javaClass.methods.firstOrNull { it.name == name }
    ?: throw NoSuchMethodException("${javaClass.name}.$name")

/**
 * Telas virtuais "de verdade" (com conteúdo próprio, não espelho): é nelas que os apps enviados
 * ao PC rodam. Criadas direto pelo IDisplayManager, como o DisplayManager faria por dentro.
 */
object VirtualDisplays {
    // Valores de DisplayManager.VIRTUAL_DISPLAY_FLAG_* (vários são ocultos).
    private const val FLAG_PUBLIC = 1 shl 0
    private const val FLAG_OWN_CONTENT_ONLY = 1 shl 3
    private const val FLAG_SUPPORTS_TOUCH = 1 shl 6
    private const val FLAG_TRUSTED = 1 shl 10
    private const val FLAG_OWN_DISPLAY_GROUP = 1 shl 11
    private const val FLAG_ALWAYS_UNLOCKED = 1 shl 12
    private const val FLAG_TOUCH_FEEDBACK_DISABLED = 1 shl 13

    /**
     * Sem FLAG_DESTROY_CONTENT_ON_REMOVAL: se a tela for removida (ou o server cair), os apps dela
     * voltam para a tela do celular em vez de serem fechados.
     * Grupo próprio + sempre desbloqueada: o app continua usável no PC com o celular bloqueado.
     */
    private const val FLAGS = FLAG_PUBLIC or FLAG_OWN_CONTENT_ONLY or FLAG_SUPPORTS_TOUCH or FLAG_TRUSTED or
        FLAG_OWN_DISPLAY_GROUP or FLAG_ALWAYS_UNLOCKED or FLAG_TOUCH_FEEDBACK_DISABLED

    private val manager by lazy { systemService("display", "android.hardware.display.IDisplayManager") }
    private val callbackStub by lazy { Class.forName("android.hardware.display.IVirtualDisplayCallback\$Stub") }

    /** Identifica a tela nas chamadas seguintes (o sistema guarda o "callback" como chave). */
    class Handle(internal val callback: Any, val displayId: Int)

    fun create(name: String, width: Int, height: Int, dpi: Int, surface: Surface?): Handle {
        val config = VirtualDisplayConfig.Builder(name, width, height, dpi).setFlags(FLAGS)
            .apply { if (surface != null) setSurface(surface) }.build()
        // Qualquer Binder serve de callback: o sistema só o usa como identificador e para saber se
        // o server morreu (aí ele remove a tela sozinho).
        val callback = callbackStub.getMethod("asInterface", IBinder::class.java).invoke(null, Binder())!!
        val displayId = manager.method("createVirtualDisplay")
            .invoke(manager, config, callback, null, "com.android.shell") as Int
        if (displayId < 0) throw IllegalStateException("O Android recusou criar a tela virtual")
        return Handle(callback, displayId)
    }

    fun resize(handle: Handle, width: Int, height: Int, dpi: Int) {
        manager.method("resizeVirtualDisplay").invoke(manager, handle.callback, width, height, dpi)
    }

    fun setSurface(handle: Handle, surface: Surface?) {
        manager.method("setVirtualDisplaySurface").invoke(manager, handle.callback, surface)
    }

    fun release(handle: Handle) {
        manager.method("releaseVirtualDisplay").invoke(manager, handle.callback)
    }
}

/** As "tarefas" do Android (cada app aberto é uma) e em qual tela cada uma está. */
object Tasks {
    const val ACTIVITY_TYPE_STANDARD = 1

    data class Task(val id: Int, val packageName: String?, val className: String?, val activityType: Int)

    private val service by lazy {
        Class.forName("android.app.ActivityTaskManager").getMethod("getService").invoke(null)!!
    }

    /** Tarefas da tela [displayId], da que está por cima para a de baixo. */
    fun list(displayId: Int, max: Int = 10): List<Task> {
        val getTasks = service.javaClass.methods.filter { it.name == "getTasks" }.maxBy { it.parameterCount }
        val result = when (getTasks.parameterCount) {
            4 -> getTasks.invoke(service, max, false, false, displayId)
            3 -> getTasks.invoke(service, 100, false, false)
            else -> getTasks.invoke(service, 100)
        } as List<*>
        return result.filterNotNull().filter { info ->
            info.javaClass.getField("displayId").getInt(info) == displayId
        }.take(max).map { info ->
            val cls = info.javaClass
            val top = cls.getField("topActivity").get(info) as android.content.ComponentName?
            val type = try {
                cls.getField("topActivityType").getInt(info)
            } catch (_: NoSuchFieldException) {
                ACTIVITY_TYPE_STANDARD
            }
            Task(cls.getField("taskId").getInt(info), top?.packageName, top?.className, type)
        }
    }

    /** O app que está na frente na tela [displayId], ignorando a tela inicial e a de apps recentes. */
    fun topApp(displayId: Int): Task? = list(displayId, 1).firstOrNull()
        ?.takeIf { it.activityType == ACTIVITY_TYPE_STANDARD && it.packageName != null }

    fun moveToDisplay(taskId: Int, displayId: Int) {
        service.method("moveRootTaskToDisplay").invoke(service, taskId, displayId)
    }

    /** Nome do app para mostrar no PC (ex.: "WhatsApp"); o nome do pacote se não der para descobrir. */
    fun label(packageName: String): String = try {
        val pm = SystemContext.get().packageManager
        pm.getApplicationLabel(pm.getApplicationInfo(packageName, 0)).toString()
    } catch (e: Exception) {
        println("Não consegui o nome de $packageName: ${e.cause ?: e}")
        packageName
    }
}

/**
 * Um Context do sistema, para as poucas APIs que exigem um (como ler o nome de um app).
 * Mesmo truque do scrcpy: cria um ActivityThread "de sistema" dentro do server. Ele precisa de
 * um Looper na thread que o cria, então é criado numa thread própria.
 */
private object SystemContext {
    private val context by lazy {
        var result: Result<android.content.Context>? = null
        thread(name = "system-context") {
            result = runCatching {
                Looper.prepare()
                val cls = Class.forName("android.app.ActivityThread")
                val activityThread = cls.getDeclaredConstructor().apply { isAccessible = true }.newInstance()
                cls.getDeclaredField("sCurrentActivityThread").apply { isAccessible = true }.set(null, activityThread)
                cls.getDeclaredField("mSystemThread").apply { isAccessible = true }.setBoolean(activityThread, true)
                // Android 13+: sem isso, getSystemContext() falha ao ler a configuração.
                try {
                    val controller = Class.forName("android.app.ConfigurationController")
                        .getDeclaredConstructor(Class.forName("android.app.ActivityThreadInternal"))
                        .apply { isAccessible = true }.newInstance(activityThread)
                    cls.getDeclaredField("mConfigurationController").apply { isAccessible = true }
                        .set(activityThread, controller)
                } catch (_: ClassNotFoundException) {
                }
                cls.getDeclaredMethod("getSystemContext").invoke(activityThread) as android.content.Context
            }
        }.join()
        result!!.getOrThrow()
    }

    fun get() = context
}

/**
 * Energia: a tela virtual de um app enviado ao PC fica no mesmo "grupo" da tela do celular
 * (testado no Android 16: o pedido de grupo próprio é ignorado), então ela apaga quando o celular
 * dorme. Por isso, enquanto houver app numa janela do PC, o celular fica acordado (tela no
 * mínimo), e é acordado se dormir e o usuário mexer na janela.
 */
object Power {
    private const val SCREEN_DIM_WAKE_LOCK = 0x6
    private const val ACQUIRE_CAUSES_WAKEUP = 0x10000000
    private const val WAKE_REASON_APPLICATION = 2

    private val service by lazy { systemService("power", "android.os.IPowerManager") }
    private val wakeLock = Binder()
    private var held = false

    fun isAwake(): Boolean = service.method("isInteractive").invoke(service) as Boolean

    fun wakeUp() {
        // wakeUp(long time, int reason, String details, String opPackageName)
        val method = service.method("wakeUp")
        val strings = ArrayDeque(listOf("Seamline", "com.android.shell"))
        val args = method.parameterTypes.mapIndexed { i, type ->
            when (type) {
                Long::class.javaPrimitiveType -> SystemClock.uptimeMillis()
                Int::class.javaPrimitiveType -> WAKE_REASON_APPLICATION
                String::class.java -> strings.removeFirstOrNull()
                else -> null
            }.also { if (i == 0 && type != Long::class.javaPrimitiveType) println("wakeUp com assinatura inesperada") }
        }
        method.invoke(service, *args.toTypedArray())
    }

    @Synchronized
    fun keepAwake(on: Boolean) {
        if (on == held) return
        if (on) {
            // acquireWakeLock(IBinder lock, int flags, String tag, String packageName, WorkSource ws,
            //                 String historyTag, int displayId, IWakeLockCallback callback)
            val method = service.method("acquireWakeLock")
            val strings = ArrayDeque(listOf("Seamline:apps", "com.android.shell", "Seamline:apps"))
            var ints = 0
            val args = method.parameterTypes.map { type ->
                when (type) {
                    IBinder::class.java -> wakeLock
                    Int::class.javaPrimitiveType -> if (ints++ == 0) SCREEN_DIM_WAKE_LOCK or ACQUIRE_CAUSES_WAKEUP else -1
                    String::class.java -> strings.removeFirstOrNull()
                    else -> null
                }
            }
            method.invoke(service, *args.toTypedArray())
        } else {
            service.method("releaseWakeLock").invoke(service, wakeLock, 0)
        }
        held = on
    }
}
