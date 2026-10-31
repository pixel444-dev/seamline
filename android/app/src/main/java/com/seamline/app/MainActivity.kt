package com.seamline.app

import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.dp

/**
 * Por enquanto o app só mostra instruções. Quem faz o trabalho pesado é o server,
 * iniciado pelo PC. Nas próximas fases ele ganha pareamento, status e permissões.
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
    Column(
        modifier = modifier.padding(24.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Text("Seamline", style = MaterialTheme.typography.headlineMedium)
        Text("${Build.MANUFACTURER} ${Build.MODEL} · Android ${Build.VERSION.RELEASE}")
        Text(
            "Para conectar, abra o Seamline no PC com o celular ligado por cabo USB " +
                "(Depuração USB) ou pela Depuração sem fio.",
        )
    }
}

@Preview(showBackground = true)
@Composable
private fun StatusScreenPreview() {
    SeamlineTheme { StatusScreen() }
}
