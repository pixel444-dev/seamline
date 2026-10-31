package com.seamline.app

import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

/**
 * Por enquanto o app só mostra instruções e abre a tela da Depuração sem fio. Quem faz o
 * trabalho pesado é o server, iniciado pelo PC. Nas próximas fases ele ganha status e permissões.
 */
class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        // Ícones da barra de status e de navegação sempre claros: o app é sempre escuro.
        enableEdgeToEdge(
            statusBarStyle = SystemBarStyle.dark(android.graphics.Color.TRANSPARENT),
            navigationBarStyle = SystemBarStyle.dark(android.graphics.Color.TRANSPARENT),
        )
        super.onCreate(savedInstanceState)
        setContent {
            SeamlineTheme {
                Surface(Modifier.fillMaxSize()) {
                    StatusScreen(Modifier.safeDrawingPadding())
                }
            }
        }
    }
}

// Cores do Seamline (as mesmas do app do PC, em desktop/Seamline.Desktop/Themes/Dark.xaml).
private val BackgroundColor = Color(0xFF0F1117)
private val CardColor = Color(0xFF171A23)
private val BorderColor = Color(0xFF262B3A)
private val TextColor = Color(0xFFE8EAF0)
private val MutedColor = Color(0xFF9AA1B4)
private val AccentColor = Color(0xFF4F7CFF)
private val AccentSoftColor = Color(0xFF1F2A4D)
private val AccentTextColor = Color(0xFF8FB0FF)
private val OnlineColor = Color(0xFF34D399)

/** Tema sempre escuro, combinando com o app do PC. */
@Composable
private fun SeamlineTheme(content: @Composable () -> Unit) {
    val colors = darkColorScheme(
        primary = AccentColor,
        onPrimary = Color.White,
        background = BackgroundColor,
        onBackground = TextColor,
        surface = BackgroundColor,
        onSurface = TextColor,
        surfaceVariant = CardColor,
        onSurfaceVariant = MutedColor,
        outline = BorderColor,
    )
    MaterialTheme(colorScheme = colors, content = content)
}

@Composable
private fun StatusScreen(modifier: Modifier = Modifier) {
    val context = LocalContext.current
    Column(
        modifier = modifier
            .verticalScroll(rememberScrollState())
            .padding(horizontal = 20.dp, vertical = 24.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp),
    ) {
        // Cabeçalho: logo e nome
        Row(verticalAlignment = Alignment.CenterVertically) {
            Image(painterResource(R.drawable.logo), contentDescription = null, modifier = Modifier.size(56.dp))
            Spacer(Modifier.width(14.dp))
            Column {
                Text("Seamline", fontSize = 26.sp, fontWeight = FontWeight.SemiBold)
                Text("Seu celular dentro do PC", color = MutedColor)
            }
        }

        DeviceChip()

        Card {
            CardHeader("Conectar pelo Wi-Fi", "SÓ NA PRIMEIRA VEZ")
            Step(1, "No PC, abra o Seamline e clique em **Parear pelo Wi-Fi**. Vai aparecer um QR code.")
            Step(
                2,
                "Toque no botão abaixo, ligue a **Depuração sem fio** e escolha " +
                    "**Parear dispositivo com código QR**. Aponte a câmera para o QR do PC.",
            )
            Button(
                onClick = { openWirelessDebugging(context) },
                modifier = Modifier.fillMaxWidth().height(52.dp),
                shape = RoundedCornerShape(12.dp),
            ) {
                Text("Abrir Depuração sem fio", fontSize = 16.sp, fontWeight = FontWeight.SemiBold)
            }
        }

        Card {
            CardHeader("Depois disso")
            Tip("O PC lembra do celular: basta a **Depuração sem fio** estar ligada. Também dá para usar o **cabo USB**.")
            Tip("Leve o mouse até a **borda esquerda do monitor** para usar o celular com o mouse e o teclado do PC.")
            Tip("Quem comanda tudo é o **Seamline do PC**. Este app pode ficar fechado.")
        }
    }
}

/** Etiqueta com o nome do aparelho. */
@Composable
private fun DeviceChip() {
    val maker = Build.MANUFACTURER.replaceFirstChar { it.uppercase() }
    Row(
        modifier = Modifier
            .background(CardColor, CircleShape)
            .padding(horizontal = 14.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(Modifier.size(8.dp).background(OnlineColor, CircleShape))
        Spacer(Modifier.width(10.dp))
        Text("$maker ${Build.MODEL} · Android ${Build.VERSION.RELEASE}", fontSize = 13.sp, color = MutedColor)
    }
}

@Composable
private fun Card(content: @Composable ColumnScope.() -> Unit) {
    Surface(
        shape = RoundedCornerShape(16.dp),
        color = CardColor,
        contentColor = TextColor,
        border = BorderStroke(1.dp, BorderColor),
        modifier = Modifier.fillMaxWidth(),
    ) {
        Column(Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(14.dp), content = content)
    }
}

@Composable
private fun CardHeader(title: String, label: String? = null) {
    Column {
        if (label != null)
            Text(label, fontSize = 11.sp, fontWeight = FontWeight.SemiBold, color = AccentTextColor, letterSpacing = 1.sp)
        Text(title, fontSize = 18.sp, fontWeight = FontWeight.SemiBold)
    }
}

/** Passo numerado das instruções. */
@Composable
private fun Step(number: Int, text: String) {
    Row {
        Box(
            Modifier.size(26.dp).background(AccentSoftColor, CircleShape),
            contentAlignment = Alignment.Center,
        ) {
            Text("$number", color = AccentTextColor, fontSize = 13.sp, fontWeight = FontWeight.SemiBold)
        }
        Spacer(Modifier.width(12.dp))
        Text(highlight(text), color = MutedColor, lineHeight = 21.sp, modifier = Modifier.padding(top = 2.dp))
    }
}

/** Dica com um marcador colorido. */
@Composable
private fun Tip(text: String) {
    Row {
        Box(Modifier.padding(top = 8.dp).size(6.dp).background(AccentColor, CircleShape))
        Spacer(Modifier.width(12.dp))
        Text(highlight(text), color = MutedColor, lineHeight = 21.sp)
    }
}

/** Deixa em destaque (branco e negrito) os trechos entre **asteriscos duplos**. */
private fun highlight(text: String): AnnotatedString = buildAnnotatedString {
    text.split("**").forEachIndexed { i, part ->
        if (i % 2 == 1)
            withStyle(SpanStyle(color = TextColor, fontWeight = FontWeight.SemiBold)) { append(part) }
        else
            append(part)
    }
}

/**
 * Abre as Opções do desenvolvedor com a linha "Depuração sem fio" em destaque (o mesmo truque do
 * Shizuku). O leitor de QR do pareamento é do próprio Android: só ele pode autorizar o PC.
 */
private fun openWirelessDebugging(context: Context) {
    val intent = Intent(Settings.ACTION_APPLICATION_DEVELOPMENT_SETTINGS)
        .putExtra(":settings:fragment_args_key", "toggle_adb_wireless")
        .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
    try {
        context.startActivity(intent)
    } catch (_: ActivityNotFoundException) {
        context.startActivity(Intent(Settings.ACTION_SETTINGS).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
    }
}

@Preview(showBackground = true, backgroundColor = 0xFF0F1117)
@Composable
private fun StatusScreenPreview() {
    SeamlineTheme { StatusScreen() }
}
