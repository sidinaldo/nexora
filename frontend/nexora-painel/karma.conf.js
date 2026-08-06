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
// `--window-size=1440,960` põe o navegador numa janela de notebook de verdade. Quem quiser medir
// uma largura menor continua fazendo o que já fazia — renderizar dentro de um `div` do tamanho
// desejado —, com a ressalva de que a media query segue olhando a janela.
// ==================================================================================
const JANELA = '--window-size=1440,960';

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
        flags: [JANELA, '--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage']
      }
    }
  });
};
