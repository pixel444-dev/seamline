# Protocolo PC ↔ celular (versão 3)

## Como a conexão é aberta

1. O PC envia `seamline-server.apk` para `/data/local/tmp/` no celular (`adb push`).
2. O PC cria um túnel: `adb forward tcp:<porta livre> localabstract:seamline`.
3. O PC inicia o server:
   `adb shell CLASSPATH=/data/local/tmp/seamline-server.apk app_process / com.seamline.server.Server 3 1600`
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
| `pointer_enter` | PC → celular | `edge` (`left`/`right`/`top`/`bottom`), `position` (0–1) | O cursor do PC entrou no celular. `edge` é a borda do celular por onde ele entra e por onde vai sair; `position` é a posição ao longo dela |
| `pointer_exit` | PC → celular | — | O PC pegou o cursor de volta (atalho Ctrl+Alt+Esc, erro, desconexão). O server solta os botões |
| `pointer_leave` | celular → PC | `position` (0–1) | A setinha encostou na borda `edge` e o usuário continuou empurrando: o cursor volta ao PC nessa posição |
| `mouse` | PC → celular | `dx`, `dy`, `buttons`, `wheel`, `hwheel` | Movimento relativo (contagens "cruas" do mouse, sem aceleração do Windows), botões apertados (bits: 1 esquerdo, 2 direito, 4 meio, 8 lateral de trás, 16 lateral da frente) e cliques da roda (positivo = para cima / para a direita) |
| `keyboard` | PC → celular | `attached` (bool) | Conecta ou remove o teclado virtual do celular |
| `keys` | PC → celular | `modifiers`, `keys` | Estado completo do teclado HID: byte de modificadores (bits: Ctrl, Shift, Alt, Win esquerdos e depois os direitos) e até 6 códigos de uso HID (página 0x07) apertados |
| `bye` | PC → celular | — | O PC vai desconectar; o server encerra |

Mensagens com `type` desconhecido, ou que derem erro, são ignoradas e registradas no log do server.

### Mouse e teclado virtuais (Fase 2)

O server cria um mouse pelo `/dev/uhid` ao conectar ("Seamline Mouse"), e um teclado ("Seamline Keyboard")
só enquanto o PC manda `keyboard` com `attached: true`. Para o Android eles são aparelhos USB comuns: a setinha
aparece, a aceleração do mouse é a do celular e as letras seguem o layout de teclado físico configurado no celular.
O teclado não fica sempre conectado porque, com um teclado físico, o Android esconde o teclado da tela.

Para saber se a setinha está na borda, o server lê a posição com `IInputManager.getCursorPositionInLogicalDisplay`
(Android 16+). Em versões antigas não há detecção: o usuário volta ao PC com Ctrl+Alt+Esc.

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
