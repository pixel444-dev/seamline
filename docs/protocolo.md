# Protocolo PC ↔ celular (versão 1)

## Como a conexão é aberta

1. O PC envia `seamline-server.apk` para `/data/local/tmp/` no celular (`adb push`).
2. O PC cria um túnel: `adb forward tcp:<porta livre> localabstract:seamline`.
3. O PC inicia o server:
   `adb shell CLASSPATH=/data/local/tmp/seamline-server.apk app_process / com.seamline.server.Server 1`
   (o último argumento é a versão do protocolo; se não bater, o server sai com código 1).
4. O server abre o socket local `seamline` e espera **uma** conexão.
5. O PC conecta em `127.0.0.1:<porta>`. O túnel aceita a conexão mesmo antes do server estar pronto
   e a fecha em seguida, então o PC tenta de novo a cada 200 ms (até 10 s) até receber o `hello`.

Funciona igual por cabo USB e pela Depuração sem fio: o adb cuida do transporte.

## Formato das mensagens (canal de controle)

Cada mensagem é um objeto JSON em UTF-8, precedido de 4 bytes com o tamanho em bytes (inteiro big-endian).
Tamanho máximo: 16 MiB. Todo objeto tem o campo `type`.

| `type` | Direção | Campos | Significado |
|---|---|---|---|
| `hello` | celular → PC | `protocol`, `manufacturer`, `model`, `android`, `sdk` | Primeira mensagem, enviada logo após a conexão |
| `ping` | PC → celular | — | Teste de ida e volta |
| `pong` | celular → PC | — | Resposta ao `ping` |
| `bye` | PC → celular | — | O PC vai desconectar; o server encerra |

Mensagens com `type` desconhecido são ignoradas (e registradas no log do server).

## Próximos passos (fase 1)

- Canal de vídeo: segunda conexão no mesmo socket, fluxo H.264 (Annex B) da tela inteira.
- Mensagens de entrada: toque/clique e teclado, com coordenadas relativas ao tamanho do vídeo.
