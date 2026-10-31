package com.seamline.app

import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.dp

/**
 * Por enquanto o app só mostra instruções e abre a tela da Depuração sem fio. Quem faz o
 * trabalho pesado é o server, iniciado pelo PC. Nas próximas fases ele ganha status e permissões.
 */
class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent {
            SeamlineTheme {
                Surface(Modifier.fillMaxSize()) {
                    StatusScreen(Modifier.safeDrawingPadding())
                }
            }
        }
    }
}

@Composable
private fun SeamlineTheme(content: @Composable () -> Unit) {
    val colors = if (isSystemInDarkTheme()) darkColorScheme() else lightColorScheme()
    MaterialTheme(colorScheme = colors, content = content)
}

@Composable
private fun StatusScreen(modifier: Modifier = Modifier) {
    val context = LocalContext.current
    Column(
        modifier = modifier.padding(24.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Text("Seamline", style = MaterialTheme.typography.headlineMedium)
        Text("${Build.MANUFACTURER} ${Build.MODEL} · Android ${Build.VERSION.RELEASE}")
        Text("Conectar pelo Wi-Fi (só na primeira vez):", style = MaterialTheme.typography.titleMedium)
        Text("1. No PC, abra o Seamline e clique em \"Parear pelo Wi-Fi...\". Vai aparecer um QR code.")
        Text(
            "2. Toque no botão abaixo, ligue a Depuração sem fio e escolha " +
                "\"Parear dispositivo com código QR\". Aponte a câmera para o QR do PC.",
        )
        Button(onClick = { openWirelessDebugging(context) }) {
            Text("Abrir Depuração sem fio")
        }
        Text(
            "Depois disso o PC lembra do celular: basta a Depuração sem fio estar ligada. " +
                "Também dá para conectar pelo cabo USB (Depuração USB).",
        )
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

@Preview(showBackground = true)
@Composable
private fun StatusScreenPreview() {
    SeamlineTheme { StatusScreen() }
}
