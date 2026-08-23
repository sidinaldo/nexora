// Só o que o builder do Angular NÃO tem como adivinhar: um launcher para runner de CI.
//
// `ChromeHeadless` puro falha ou fica intermitente em runner por causa do sandbox do Chrome,
// que depende de permissões que o ambiente de CI normalmente não dá. As flags abaixo são as
// três que resolvem isso, e ficam ISOLADAS num launcher próprio DE PROPÓSITO: rodar sem
// sandbox é aceitável num runner descartável executando código do próprio repositório, e não
// é aceitável na máquina de ninguém. Quem roda `npm test` localmente continua no ChromeHeadless
// normal, com sandbox.
//
// `--disable-dev-shm-usage`: o /dev/shm de container é pequeno (64MB) e o Chrome trava sem
// aviso ao estourá-lo — o sintoma é um teste que "às vezes" não termina.
// `frameworks` e `plugins` precisam ser declarados: assim que existe um karma.conf.js, ele passa
// a ser a base e o padrão do builder não entra mais. Sem isto o Jasmine não carrega e todo
// arquivo de teste morre com "describe is not defined".
//
// ===================== A JANELA PRECISA SER DE DESKTOP (DES-3) =====================
// O `ChromeHeadless` padrão abre em 800x600, e o viewport útil ficava em 747x428. Isso tinha uma
// consequência que o DES-1 registrou e não conseguiu resolver: MEDIA QUERY RESPONDE AO VIEWPORT,
// não ao container. Com 747px de largura, `@media (max-width: 980px)` estava SEMPRE ativa em
// todo teste — mesmo os que renderizam a tela dentro de uma caixa de 1400px.
//
// Ou seja: os testes de layout mediam o layout de TABLET achando que mediam o de desktop, e as
// quebras de 620px e 720px nunca foram exercitadas por ninguém.
//
// `--window-size=1440,960` põe o navegador numa janela de notebook de verdade.
// ==================================================================================
const JANELA = '--window-size=1440,960';

// ===================== E A SEGUNDA JANELA, DE CELULAR (MOB-2) =====================
// O DES-3 consertou metade do problema e deixou a outra: com a janela em 1440px, NENHUM teste
// exercitava o que acontece abaixo de 860px. A caixa de entrada escondia o painel da conversa
// com `display: none` nessa faixa — a tela mais usada do produto não abria nada no celular — e
// passou despercebido porque o único teste que chegava perto media o layout de DESKTOP espremido
// numa caixa de 380px, e dizia isso em comentário. Comentário não reprova build.
//
// ⚠️ POR QUE DUAS EXECUÇÕES, E NÃO REDIMENSIONAR POR SUÍTE.
// Não há como. O Chrome recusa `window.resizeTo` em janela que o script não abriu, então nenhuma
// suíte consegue trocar o próprio viewport de dentro do karma. E media query responde à JANELA:
// renderizar dentro de um `div` de 390px continua medindo o layout que a janela de 1440px monta.
// A largura do navegador é a única coisa que decide, e ela se escolhe no lançamento.
//
// A execução de celular carrega SÓ os `*.celular.spec.ts` (ver angular.json). Rodar as 26 suítes
// duas vezes dobraria o tempo sem cobrir nada novo, e quebraria as que medem desktop de
// propósito: `larguras.spec.ts` mede numa caixa de 1400px e `lateral.spec.ts` conta os itens da
// barra lateral, que em 390px nem existe mais.
//
// ===== O PISO DE LARGURA DO CHROME, MEDIDO =====
// `--window-size=390,844` NÃO entrega 390px. O Chrome headless no Windows trava a janela num
// mínimo: pedindo 360, 390, 480 ou 500 o resultado é sempre o mesmo — 504px de janela, e ~489px
// de viewport dentro do iframe do karma. O headless antigo aceitava qualquer tamanho e foi
// removido no Chrome 132; daqui em diante o piso é fato.
//
// O pedido continua 390 de propósito: é a intenção registrada, e o dia em que o piso cair a
// execução passa a valer sem ninguém precisar lembrar.
//
// O que isso custa, e o que NÃO custa:
//   • as media queries do produto (860px) ficam ATIVAS — 489 < 860. É o que faltava, e é o que
//     faz a caixa de entrada ser mensurável pela primeira vez;
//   • ⚠️ nenhuma media query ABAIXO de ~489px pode ser testada. O produto não tem nenhuma, e
//     este comentário é o motivo para continuar assim;
//   • a largura-alvo de 390px é aplicada pela CAIXA em que cada suíte renderiza. Isso agora é
//     legítimo: o layout que está sendo espremido já é o de celular, não o de desktop.
//
// ===== E O PONTEIRO GROSSO =====
// `@media (pointer: coarse)` é o que separa dedo de mouse, e o headless nasce `fine` — as regras
// de alvo de toque simplesmente não valeriam, e o teste delas passaria medindo o layout de
// mouse. `--blink-settings` resolve: `primaryPointerType=2` é coarse, `primaryHoverType=1` é
// "não passa o mouse". Verificado em `janela.celular.spec.ts`, que reprova a execução inteira se
// qualquer uma das duas premissas cair.
// =================================================================================
const JANELA_CELULAR = [
  '--window-size=390,844',
  '--blink-settings=primaryPointerType=2,availablePointerTypes=2,primaryHoverType=1,availableHoverTypes=1'
];

// As três flags de runner descartável, uma vez só — elas valem para as duas larguras.
const CI = ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage'];

module.exports = function (config) {
  config.set({
    frameworks: ['jasmine'],
    plugins: [
      require('karma-jasmine'),
      require('karma-chrome-launcher'),
      require('karma-jasmine-html-reporter')
    ],
    reporters: ['progress'],
    browsers: ['ChromeHeadlessDesktop'],
    customLaunchers: {
      ChromeHeadlessDesktop: {
        base: 'ChromeHeadless',
        flags: [JANELA]
      },
      ChromeHeadlessCI: {
        base: 'ChromeHeadless',
        flags: [JANELA, ...CI]
      },
      ChromeHeadlessCelular: {
        base: 'ChromeHeadless',
        flags: [...JANELA_CELULAR]
      },
      ChromeHeadlessCelularCI: {
        base: 'ChromeHeadless',
        flags: [...JANELA_CELULAR, ...CI]
      }
    }
  });
};
