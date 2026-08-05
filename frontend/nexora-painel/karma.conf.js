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
module.exports = function (config) {
  config.set({
    frameworks: ['jasmine'],
    plugins: [
      require('karma-jasmine'),
      require('karma-chrome-launcher'),
      require('karma-jasmine-html-reporter')
    ],
    reporters: ['progress'],
    browsers: ['ChromeHeadless'],
    customLaunchers: {
      ChromeHeadlessCI: {
        base: 'ChromeHeadless',
        flags: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage']
      }
    }
  });
};
