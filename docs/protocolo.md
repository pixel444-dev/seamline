# Protocolo PC ↔ celular (versão 2)

## Como a conexão é aberta

1. O PC envia `seamline-server.apk` para `/data/local/tmp/` no celular (`adb push`).
2. O PC cria um túnel: `adb forward tcp:<porta livre> localabstract:seamline`.
3. O PC inicia o server:
   `adb shell CLASSPATH=/data/local/tmp/seamline-server.apk app_process / com.seamline.server.Server 2 1600`
   - 1º argumento: versão do protocolo. Se não bater, o server sai com código 1.
   - 2º argumento (opcional): maior lado do vídeo em pixels (padrão 1600).
4. O server abre o socket local `seamline` e aceita **duas** conexões, nesta ordem:
   1. **controle**: o server manda o `hello` logo ao aceitar;
   2. **vídeo**: o server começa a mandar a tela.
5. O túnel do adb aceita a conexão mesmo antes do server estar pronto e a fecha em seguida, então o PC
   tenta a conexão de controle de novo a cada 200 ms (até 10 s) até receber o `hello`. Só depois abre a de vídeo.

Funciona igual por cabo USB e pela Depuração sem fio: o adb cuida do transporte.
Se qualquer conexão cair, o server encerra.

## Conexão de controle

Cada mensagem é um objeto JSON em UTF-8, precedido de 4 bytes com o tamanho em bytes (inteiro big-endian).
Tamanho máximo: 16 MiB. Todo objeto tem o campo `type`. Coordenadas `x`/`y` vão de 0 a 1
(proporção da largura/altura da tela), assim não dependem do tamanho da janela nem do vídeo.

| `type` | Direção | Campos | Significado |
|---|---|---|---|
| `hello` | celular → PC | `protocol`, `manufacturer`, `model`, `android`, `sdk`, `screenWidth`, `screenHeight` | Primeira mensagem |
| `ping` | PC → celular | — | Teste de ida e volta |
| `pong` | celular → PC | — | Resposta ao `ping` |
| `touch` | PC → celular | `action` (`down`/`move`/`up`), `x`, `y` | Toque de um dedo |
| `scroll` | PC → celular | `x`, `y`, `dx`, `dy` | Rolagem; `dy` em "cliques" da roda, positivo = para cima |
| `key` | PC → celular | `key`: `back`, `home`, `app_switch`, `power` | Aperta e solta uma tecla do sistema |
| `bye` | PC → celular | — | O PC vai desconectar; o server encerra |

Mensagens com `type` desconhecido, ou que derem erro, são ignoradas e registradas no log do server.

## Conexão de vídeo (binária, celular → PC)

1. Cabeçalho: largura (int32) e altura (int32) do vídeo, big-endian. Sempre múltiplos de 16.
2. Depois, pacotes H.264 (Annex B), cada um com:
   - 8 bytes: tempo de apresentação em µs; bit 63 = pacote de configuração (SPS/PPS), bit 62 = quadro-chave;
   - 4 bytes: tamanho dos dados;
   - os dados.

O pacote de configuração vem primeiro; o PC o junta ao pacote seguinte antes de decodificar.
Com a tela parada, o codificador repete o último quadro a cada 100 ms.

Limitação atual: o tamanho do vídeo é fixo ao conectar. Se o celular girar, a imagem fica com bordas
pretas e os toques ficam desalinhados (a corrigir).
