# Protocolo PC ↔ celular (versão 4)

## Como a conexão é aberta

1. O PC envia `seamline-server.apk` para `/data/local/tmp/` no celular (`adb push`).
2. O PC cria um túnel: `adb forward tcp:<porta livre> localabstract:seamline`.
3. O PC inicia o server:
   `adb shell CLASSPATH=/data/local/tmp/seamline-server.apk app_process / com.seamline.server.Server 4 1600`
   - 1º argumento: versão do protocolo. Se não bater, o server sai com código 1.
   - 2º argumento (opcional): maior lado do vídeo em pixels (padrão 1600).
4. O server abre o socket local `seamline` e aceita, nesta ordem:
   1. **controle**: o server manda o `hello` logo ao aceitar;
   2. **vídeo**: o server começa a mandar a tela;
   3. depois, uma conexão de **vídeo de janela de app** para cada `app_open` (Fase 3): o PC conecta e
      manda o id da janela (int32 big-endian); o server responde com o vídeo da tela virtual dela.
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
| `pointer_leave` | celular → PC | `position` (0–1), `drag` (opcional) | A setinha encostou na borda `edge` e o usuário continuou empurrando: o cursor volta ao PC nessa posição. Com o botão esquerdo apertado (arrastando), `drag` diz o que vem junto: `app` (o app da frente) ou `pc_window` (a janela do PC que estava no celular) |
| `mouse` | PC → celular | `dx`, `dy`, `buttons`, `wheel`, `hwheel` | Movimento relativo (contagens "cruas" do mouse, sem aceleração do Windows), botões apertados (bits: 1 esquerdo, 2 direito, 4 meio, 8 lateral de trás, 16 lateral da frente) e cliques da roda (positivo = para cima / para a direita) |
| `keyboard` | PC → celular | `attached` (bool) | Conecta ou remove o teclado virtual do celular |
| `keys` | PC → celular | `modifiers`, `keys` | Estado completo do teclado HID: byte de modificadores (bits: Ctrl, Shift, Alt, Win esquerdos e depois os direitos) e até 6 códigos de uso HID (página 0x07) apertados |
| `app_open` | PC → celular | `id`, `width`, `height` (pixels), `dpi` | Leva o app da frente no celular para uma tela virtual desse tamanho (janela `id` do PC). Depois o PC abre a conexão de vídeo da janela |
| `app_resize` | PC → celular | `id`, `width`, `height` | A janela mudou de tamanho: a tela virtual muda junto (múltiplos de 16) |
| `app_close` | PC → celular | `id` | Fechou a janela: o app volta para a tela do celular |
| `app_opened` | celular → PC | `id`, `package`, `label` | O app foi para a janela (`label` = nome do app) |
| `app_error` | celular → PC | `id`, `message` | Não deu para abrir (ex.: só a tela inicial estava aberta) |
| `app_closed` | celular → PC | `id` | A janela ficou vazia (o app fechou ou foi aberto de novo no celular) |
| `bye` | PC → celular | — | O PC vai desconectar; o server encerra |

`touch`, `scroll` e `key` aceitam o campo opcional `window` (id de uma janela de app): o evento vai para a
tela virtual daquela janela, com `x`/`y` relativos a ela.

Mensagens com `type` desconhecido, ou que derem erro, são ignoradas e registradas no log do server.

### Mouse e teclado virtuais (Fase 2)

O server cria um mouse pelo `/dev/uhid` ao conectar ("Seamline Mouse"), e um teclado ("Seamline Keyboard")
só enquanto o PC manda `keyboard` com `attached: true`. Para o Android eles são aparelhos USB comuns: a setinha
aparece, a aceleração do mouse é a do celular e as letras seguem o layout de teclado físico configurado no celular.
O teclado não fica sempre conectado porque, com um teclado físico, o Android esconde o teclado da tela.

Para saber se a setinha está na borda, o server lê a posição com `IInputManager.getCursorPositionInLogicalDisplay`
(Android 16+). Em versões antigas não há detecção: o usuário volta ao PC com Ctrl+Alt+Esc.

### Janelas de app (Fase 3)

Para cada janela, o server cria uma tela virtual (sem `DESTROY_CONTENT_ON_REMOVAL`: ao removê-la, ou se o
server cair, o app volta para a tela do celular) e move a tarefa do app para lá
(`IActivityTaskManager.moveRootTaskToDisplay`). No Android 16 testado, o pedido de "grupo de tela próprio"
é ignorado, então a tela virtual dorme junto com o celular: enquanto houver janela de app, o server segura
um wake lock (`SCREEN_DIM`, a tela do celular fica acesa no mínimo) e acorda o celular se o PC mexer na janela.

## Conexão de vídeo (binária, celular → PC)

1. Cabeçalho: largura (int32) e altura (int32) do vídeo, big-endian. Sempre múltiplos de 16.
2. Depois, pacotes H.264 (Annex B), cada um com:
   - 8 bytes: tempo de apresentação em µs; bit 63 = pacote de configuração (SPS/PPS), bit 62 = quadro-chave,
     bit 61 = **novo tamanho** (os dados são largura e altura, int32 cada: o PC recria o decodificador);
   - 4 bytes: tamanho dos dados;
   - os dados.

O pacote de configuração vem primeiro; o PC o junta ao pacote seguinte antes de decodificar.
Com a tela parada, o codificador repete o último quadro a cada 100 ms.

Limitação atual: o tamanho do vídeo é fixo ao conectar. Se o celular girar, a imagem fica com bordas
pretas e os toques ficam desalinhados (a corrigir).

## Janela do PC no celular (Fase 5)

Não passa pelo server: o PC fala direto com o app Seamline (`com.seamline.app`).

1. O PC instala/atualiza o app se o `.apk` mudou (guarda um hash em `/data/local/tmp/seamline-app.version`).
2. O PC abre uma porta local e `adb reverse tcp:<porta> tcp:<porta>`.
3. O PC roda `am start -n com.seamline.app/.RemoteWindowActivity --ei port <porta>`; a tela conecta em `127.0.0.1:<porta>`.
4. PC → celular: o mesmo formato da conexão de vídeo acima (cabeçalho + pacotes, com "novo tamanho").
   H.264 Baseline do codificador por software do Windows, até 30 quadros/s, maior lado até 1920 px.
5. Celular → PC: mensagens JSON com 4 bytes de tamanho na frente:

| `type` | Campos | Significado |
|---|---|---|
| `pointer` | `action` (`down`/`move`/`up`/`hover`/`scroll`/`right_down`/`right_up`), `x`, `y` (0–1), `dx`/`dy` na rolagem | Toque ou mouse na imagem da janela |
| `key` | `down`, `code` (KEYCODE do Android), `char`, `ctrl`, `alt`, `shift` | Tecla (do teclado do PC, com o cursor no celular) |
| `keyframe` | — | A tela do celular recriou o decodificador: o PC manda configuração + quadro completo |

No PC, a janela fica no lugar, 100% transparente e atravessável pelo mouse (fora da tela o Windows para de
redesenhá-la). Toques e teclas viram mensagens para a janela (`PostMessage`), sem mexer no cursor de verdade.
Quando a conexão fecha (Voltar no celular, arraste para fora, janela fechada), a janela reaparece.

## Painel gamer no celular

Mesmo caminho da janela do PC no celular (instala/atualiza o app, `adb reverse`), mas abre
`am start -n com.seamline.app/.GamerPanelActivity --ei port <porta>` e **não manda vídeo**: o PC manda
uma mensagem JSON (4 bytes de tamanho na frente) por segundo e o celular desenha o painel.
O celular não manda nada; quando ele fecha a conexão (Voltar), o PC encerra.

`type: "metrics"` — números em `null` quando não estão disponíveis:

| Campo | Significado |
|---|---|
| `cpu_name`, `cpu_usage` (%), `cpu_clock` (MHz), `cpu_temp` (°C) | Processador |
| `cpu_temp_status` | `ok`, `admin` (abrir como administrador), `pawnio` (instalar o driver PawnIO) ou `none` |
| `gpu_name`, `gpu_usage` (%), `gpu_temp` (°C), `gpu_fan` (rpm; 0 = parada), `gpu_power` (% do máximo) | Placa de vídeo |
| `vram_used`, `vram_total`, `ram_used`, `ram_total` | Memórias, em MB |
| `fps`, `frame_ms`, `fps_low` (1% low dos últimos 10 s), `game` | Jogo em primeiro plano |
| `fps_status` | `ok`, `admin` ou `error` |
| `admin` | O Seamline do PC está aberto como administrador |
