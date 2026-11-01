package com.seamline.app

import android.content.Intent
import android.graphics.Typeface
import android.os.Bundle
import android.view.WindowInsets
import android.view.WindowInsetsController
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.Shadow
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import org.json.JSONObject
import java.io.DataInputStream
import java.io.IOException
import java.net.InetSocketAddress
import java.net.Socket
import kotlin.concurrent.thread
import kotlin.math.cos
import kotlin.math.max
import kotlin.math.min
import kotlin.math.sin

/**
 * Painel gamer: mostra as métricas do PC (FPS, CPU, GPU, RAM, VRAM e temperaturas) para deixar o
 * celular ao lado do monitor enquanto joga.
 *
 * Igual à janela do PC no celular: o PC abre esta tela com `--ei port <porta>` e um túnel
 * "adb reverse". Só que aqui chega uma mensagem JSON pequena por segundo ("metrics", veja
 * docs/protocolo.md) e o desenho é feito aqui mesmo, sem vídeo. Voltar fecha o painel.
 */
class GamerPanelActivity : ComponentActivity() {
    private val metrics = mutableStateOf<PcMetrics?>(null)
    private val fpsHistory = mutableStateListOf<Float>()
    private var socket: Socket? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setShowWhenLocked(true)
        setTurnScreenOn(true)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        setContent { GamerPanel(metrics.value, fpsHistory) }
        // Tela cheia (só depois do setContent: antes disso a janela ainda não tem o controlador).
        window.insetsController?.let {
            it.hide(WindowInsets.Type.systemBars())
            it.systemBarsBehavior = WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        }
        connect(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        connect(intent)
    }

    override fun onDestroy() {
        super.onDestroy()
        closeSocket()
    }

    private fun closeSocket() {
        try {
            socket?.close()
        } catch (_: IOException) {
        }
        socket = null
    }

    private fun connect(intent: Intent) {
        val port = intent.getIntExtra("port", 0)
        if (port == 0) {
            finish()
            return
        }
        closeSocket()
        val socket = Socket().also { this.socket = it }
        thread(name = "gamer-panel") {
            try {
                socket.connect(InetSocketAddress("127.0.0.1", port), 5000)
                socket.tcpNoDelay = true
                val input = DataInputStream(socket.getInputStream())
                while (true) {
                    val length = input.readInt()
                    if (length < 0 || length > 1 shl 20) break
                    val bytes = ByteArray(length)
                    input.readFully(bytes)
                    val json = JSONObject(String(bytes, Charsets.UTF_8))
                    if (json.optString("type") != "metrics") continue
                    val m = PcMetrics.from(json)
                    runOnUiThread {
                        metrics.value = m
                        if (m.fpsStatus == "ok") {
                            fpsHistory.add((m.fps ?: 0.0).toFloat())
                            if (fpsHistory.size > HISTORY) fpsHistory.removeAt(0)
                        }
                    }
                }
            } catch (_: IOException) {
            } catch (_: org.json.JSONException) {
            }
            // O PC desconectou (ou este socket foi trocado por outro): só fecha se ainda for o atual.
            runOnUiThread { if (this.socket === socket) finish() }
        }
    }

    companion object {
        const val HISTORY = 60
    }
}

/** Uma leitura do PC. null = não disponível (sem sensor ou sem permissão). */
data class PcMetrics(
    val cpuName: String, val cpuUsage: Double, val cpuClock: Double?, val cpuTemp: Double?, val cpuTempStatus: String,
    val gpuName: String, val gpuUsage: Double?, val gpuTemp: Double?, val gpuFan: Double?, val gpuPower: Double?,
    val vramUsed: Double?, val vramTotal: Double, val ramUsed: Double, val ramTotal: Double,
    val fps: Double?, val frameMs: Double?, val fpsLow: Double?, val game: String, val fpsStatus: String,
) {
    companion object {
        private fun JSONObject.num(key: String): Double? = if (isNull(key)) null else optDouble(key).takeIf { !it.isNaN() }

        fun from(j: JSONObject) = PcMetrics(
            j.optString("cpu_name"), j.num("cpu_usage") ?: 0.0, j.num("cpu_clock"), j.num("cpu_temp"), j.optString("cpu_temp_status"),
            j.optString("gpu_name"), j.num("gpu_usage"), j.num("gpu_temp"), j.num("gpu_fan"), j.num("gpu_power"),
            j.num("vram_used"), j.num("vram_total") ?: 0.0, j.num("ram_used") ?: 0.0, j.num("ram_total") ?: 1.0,
            j.num("fps"), j.num("frame_ms"), j.num("fps_low"), j.optString("game"), j.optString("fps_status"),
        )
    }
}

// Visual "gamer": quase preto, neon e números grandes (as mesmas cores do painel no PC).
private val PanelBackground = Color(0xFF07080D)
private val CardColor = Color(0xFF0D0F17)
private val TileColor = Color(0xFF141826)
private val TrackColor = Color(0xFF1C202E)
private val LabelColor = Color(0xFF7E869E)
private val TextColor = Color(0xFFE8EAF0)
private val FpsColor = Color(0xFF7CFF4F)
private val Cyan = Color(0xFF00E5FF)
private val Blue = Color(0xFF3D7BFF)
private val Pink = Color(0xFFFF2E88)
private val Purple = Color(0xFFA855F7)
private val Warm = Color(0xFFFFC400)
private val Hot = Color(0xFFFF3B5C)
private val NumberFont = FontFamily(Typeface.create("sans-serif-condensed", Typeface.BOLD))

private fun heatColor(celsius: Double) = when {
    celsius >= 85 -> Hot
    celsius >= 70 -> Warm
    else -> Cyan
}

private fun fpsHint(m: PcMetrics): String? = when {
    m.fpsStatus == "admin" -> "Para ver o FPS, no PC: Painel gamer → \"Ativar FPS e temp. da CPU\"."
    m.fpsStatus == "error" -> "O PC não conseguiu ligar a contagem de FPS."
    (m.fps ?: 0.0) <= 0 -> "Abra um jogo (DirectX 9 a 12) para ver o FPS."
    else -> null
}

private fun cpuTempHint(status: String): String? = when (status) {
    "admin" -> "Temperatura: abra o Seamline do PC como administrador."
    "pawnio" -> "Temperatura: instale o PawnIO no PC (pawnio.eu)."
    "none" -> "Este processador não informou a temperatura."
    else -> null
}

@Composable
private fun GamerPanel(m: PcMetrics?, history: List<Float>) {
    val background = Brush.radialGradient(listOf(Cyan.copy(alpha = 0.10f), Color.Transparent), center = Offset(0f, 0f), radius = 1400f)
    BoxWithConstraints(
        Modifier
            .fillMaxSize()
            .background(PanelBackground)
            .background(background)
            .padding(14.dp),
    ) {
        if (m == null) {
            Text("Conectando ao PC...", color = LabelColor, modifier = Modifier.align(Alignment.Center))
            return@BoxWithConstraints
        }
        val landscape = maxWidth > maxHeight
        val gap = 10.dp
        Column(verticalArrangement = Arrangement.spacedBy(gap)) {
            if (landscape) {
                Row(Modifier.weight(1f), horizontalArrangement = Arrangement.spacedBy(gap)) {
                    FpsCard(m, history, Modifier.weight(1.2f).fillMaxHeight())
                    CpuCard(m, Modifier.weight(1f).fillMaxHeight())
                    GpuCard(m, Modifier.weight(1f).fillMaxHeight())
                }
                Row(horizontalArrangement = Arrangement.spacedBy(gap)) { MemoryCards(m) }
            } else {
                FpsCard(m, history, Modifier.weight(0.9f).fillMaxWidth())
                Row(Modifier.weight(1f), horizontalArrangement = Arrangement.spacedBy(gap)) {
                    CpuCard(m, Modifier.weight(1f).fillMaxHeight())
                    GpuCard(m, Modifier.weight(1f).fillMaxHeight())
                }
                Row(horizontalArrangement = Arrangement.spacedBy(gap)) { MemoryCards(m) }
            }
        }
    }
}

@Composable
private fun Card(modifier: Modifier = Modifier, content: @Composable () -> Unit) {
    val shape = RoundedCornerShape(14.dp)
    Box(
        modifier
            .clip(shape)
            .background(CardColor)
            .border(1.dp, Brush.linearGradient(listOf(Color(0x33FFFFFF), Color(0x0AFFFFFF), Color(0x1AFFFFFF))), shape)
            .padding(horizontal = 14.dp, vertical = 12.dp),
    ) { content() }
}

@Composable
private fun Label(text: String, color: Color = LabelColor, size: TextUnit = 11.sp) =
    Text(text, color = color, fontSize = size, fontFamily = NumberFont, maxLines = 1, overflow = TextOverflow.Ellipsis)

@Composable
private fun Hint(text: String?) {
    if (text != null) Text(text, color = LabelColor, fontSize = 10.sp, lineHeight = 13.sp, maxLines = 2)
}

@Composable
private fun Tile(label: String, value: String?, unit: String = "", modifier: Modifier = Modifier, color: Color = TextColor) {
    Column(modifier.clip(RoundedCornerShape(10.dp)).background(TileColor).padding(horizontal = 10.dp, vertical = 6.dp)) {
        Label(label, size = 10.sp)
        // Número grande e unidade pequena, para caber nos quadradinhos estreitos do celular em pé.
        val text = buildAnnotatedString {
            append(value ?: "--")
            if (value != null && unit.isNotEmpty()) withStyle(SpanStyle(fontSize = 11.sp, color = LabelColor)) { append(" $unit") }
        }
        Text(text, color = if (value == null) LabelColor else color, fontSize = 20.sp, fontFamily = NumberFont, maxLines = 1, softWrap = false)
    }
}

@Composable
private fun FpsCard(m: PcMetrics, history: List<Float>, modifier: Modifier) {
    val hasGame = (m.fps ?: 0.0) > 0
    Card(modifier) {
        Column {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Label("FPS", FpsColor, 13.sp)
                Spacer(Modifier.width(10.dp))
                Text(m.game, color = LabelColor, fontSize = 11.sp, maxLines = 1, overflow = TextOverflow.Ellipsis,
                    textAlign = TextAlign.End, modifier = Modifier.weight(1f))
            }
            Box(Modifier.weight(1f).fillMaxWidth(), contentAlignment = Alignment.Center) {
                Text(
                    if (hasGame) "%.0f".format(m.fps) else "--",
                    style = TextStyle(
                        color = FpsColor.copy(alpha = if (hasGame) 1f else 0.35f),
                        fontSize = 84.sp,
                        fontFamily = NumberFont,
                        shadow = Shadow(FpsColor.copy(alpha = 0.7f), blurRadius = 40f),
                    ),
                )
                Box(Modifier.align(Alignment.BottomCenter)) { Hint(fpsHint(m)) }
            }
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Tile("TEMPO DO QUADRO", if (hasGame) "%.1f".format(m.frameMs) else null, "ms", Modifier.weight(1f))
                Tile("1% LOW", if (hasGame) "%.0f".format(m.fpsLow) else null, "fps", Modifier.weight(1f))
            }
            Sparkline(history, Modifier.fillMaxWidth().height(64.dp).padding(top = 8.dp))
        }
    }
}

@Composable
private fun CpuCard(m: PcMetrics, modifier: Modifier) {
    Card(modifier) {
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            Column(Modifier.fillMaxWidth()) {
                Label("CPU", Cyan, 13.sp)
                Label(m.cpuName, size = 10.sp)
            }
            Gauge(m.cpuUsage, Cyan, Blue, Modifier.weight(1f).padding(vertical = 6.dp))
            Hint(cpuTempHint(m.cpuTempStatus))
            Row(horizontalArrangement = Arrangement.spacedBy(6.dp), modifier = Modifier.padding(top = 4.dp)) {
                Tile("TEMP.", m.cpuTemp?.let { "%.0f°".format(it) }, "C", Modifier.weight(1f), m.cpuTemp?.let { heatColor(it) } ?: TextColor)
                Tile("RELÓGIO", m.cpuClock?.let { "%.2f".format(it / 1000) }, "GHz", Modifier.weight(1f))
            }
        }
    }
}

@Composable
private fun GpuCard(m: PcMetrics, modifier: Modifier) {
    Card(modifier) {
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            Column(Modifier.fillMaxWidth()) {
                Label("GPU", Pink, 13.sp)
                Label(m.gpuName, size = 10.sp)
            }
            Gauge(m.gpuUsage ?: 0.0, Pink, Purple, Modifier.weight(1f).padding(vertical = 6.dp))
            Hint(m.gpuPower?.let { "Consumo: %.0f%% do máximo".format(it) })
            Row(horizontalArrangement = Arrangement.spacedBy(6.dp), modifier = Modifier.padding(top = 4.dp)) {
                Tile("TEMP.", m.gpuTemp?.let { "%.0f°".format(it) }, "C", Modifier.weight(1f), m.gpuTemp?.let { heatColor(it) } ?: TextColor)
                val fan = m.gpuFan
                Tile("VENTOINHA", when {
                    fan == null -> null
                    fan > 0 -> "%.0f".format(fan)
                    else -> "parada"
                }, if (fan != null && fan > 0) "rpm" else "", Modifier.weight(1f))
            }
        }
    }
}

@Composable
private fun RowScope.MemoryCards(m: PcMetrics) {
    MemoryBar("MEMÓRIA RAM", m.ramUsed, m.ramTotal, Cyan, FpsColor, Modifier.weight(1f))
    MemoryBar("VRAM", m.vramUsed, m.vramTotal, Pink, Purple, Modifier.weight(1f))
}

@Composable
private fun MemoryBar(label: String, used: Double?, total: Double, start: Color, end: Color, modifier: Modifier) {
    val fraction by animateFloatAsState(
        if (used != null && total > 0) (used / total).toFloat().coerceIn(0f, 1f) else 0f, tween(600), label = label,
    )
    Card(modifier) {
        Column {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(Modifier.weight(1f)) { Label(label, size = 11.sp) }
                Text(
                    if (used != null && total > 0) "%.1f / %.1f GB".format(used / 1024, total / 1024) else "--",
                    color = TextColor, fontSize = 14.sp, fontFamily = NumberFont,
                )
            }
            Box(Modifier.padding(top = 8.dp).fillMaxWidth().height(8.dp).clip(RoundedCornerShape(4.dp)).background(TrackColor)) {
                Box(
                    Modifier.fillMaxHeight().fillMaxWidth(fraction).clip(RoundedCornerShape(4.dp))
                        .background(Brush.horizontalGradient(listOf(start, end))),
                )
            }
        }
    }
}

/** Medidor em arco (270°) com brilho, de 0 a 100%, com o número no meio. */
@Composable
private fun Gauge(value: Double, start: Color, end: Color, modifier: Modifier) {
    val animated by animateFloatAsState(value.toFloat().coerceIn(0f, 100f), tween(600), label = "gauge")
    // O arco usa o menor lado da área (o Canvas ocupa tudo e desenha centralizado).
    Box(modifier.fillMaxWidth(), contentAlignment = Alignment.Center) {
        Canvas(Modifier.fillMaxSize()) {
            val stroke = size.minDimension * 0.075f
            val inset = stroke * 1.2f
            val arcSize = Size(size.minDimension - inset * 2, size.minDimension - inset * 2)
            val topLeft = Offset((size.width - arcSize.width) / 2, (size.height - arcSize.height) / 2)
            drawArc(TrackColor, 135f, 270f, false, topLeft, arcSize, style = Stroke(stroke, cap = StrokeCap.Round))
            // Marquinhas a cada 10%
            val center = Offset(size.width / 2, size.height / 2)
            val radius = arcSize.width / 2 - stroke - 4.dp.toPx()
            for (i in 0..10) {
                val angle = Math.toRadians(135.0 + 27.0 * i)
                val length = if (i % 5 == 0) 8.dp.toPx() else 4.dp.toPx()
                val dir = Offset(cos(angle).toFloat(), sin(angle).toFloat())
                drawLine(LabelColor.copy(alpha = 0.4f), center + dir * radius, center + dir * (radius - length), 1.5f)
            }
            if (animated > 0.5f) {
                val brush = Brush.linearGradient(listOf(start, end), start = Offset(0f, size.height), end = Offset(size.width, 0f))
                val sweep = 270f * animated / 100f
                drawArc(brush, 135f, sweep, false, topLeft, arcSize, alpha = 0.25f, style = Stroke(stroke * 2.2f, cap = StrokeCap.Round))
                drawArc(brush, 135f, sweep, false, topLeft, arcSize, style = Stroke(stroke, cap = StrokeCap.Round))
            }
        }
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            Text("%.0f%%".format(value), color = TextColor, fontSize = 30.sp, fontFamily = NumberFont)
            Label("USO", size = 10.sp)
        }
    }
}

/** Histórico do FPS (último minuto), linha neon com área em degradê embaixo. */
@Composable
private fun Sparkline(values: List<Float>, modifier: Modifier) {
    Canvas(modifier) {
        for (i in 1..3) {
            val y = size.height * i / 4
            drawLine(LabelColor.copy(alpha = 0.13f), Offset(0f, y), Offset(size.width, y), 1f)
        }
        if (values.size < 2) return@Canvas
        val top = max(30f, values.max() * 1.15f)
        val step = size.width / (GamerPanelActivity.HISTORY - 1)
        val offset = (GamerPanelActivity.HISTORY - values.size) * step
        fun point(i: Int) = Offset(offset + i * step, size.height - 2f - (size.height - 4f) * min(values[i] / top, 1f))
        val line = Path().apply {
            moveTo(point(0).x, point(0).y)
            for (i in 1 until values.size) lineTo(point(i).x, point(i).y)
        }
        val area = Path().apply {
            addPath(line)
            lineTo(point(values.size - 1).x, size.height)
            lineTo(offset, size.height)
            close()
        }
        drawPath(area, Brush.verticalGradient(listOf(FpsColor.copy(alpha = 0.33f), Color.Transparent)))
        drawPath(line, FpsColor.copy(alpha = 0.3f), style = Stroke(5.dp.toPx(), join = StrokeJoin.Round))
        drawPath(line, FpsColor, style = Stroke(2.dp.toPx(), join = StrokeJoin.Round))
    }
}
