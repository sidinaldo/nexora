import { minutosUteis, JanelaAtendimento } from './semaforo';

// O MESMO arquivo que `ParidadeMinutosUteisTests.cs` lê. Não é cópia: é o mesmo caminho no
// disco, importado de fora do `src` de propósito. Duplicá-lo aqui derrotaria o propósito
// inteiro — os dois lados poderiam divergir junto com suas cópias, verdes até o fim.
import casos from '../../../../../tests/paridade/minutos-uteis.json';

/** PARIDADE com `TempoUtil.MinutosUteis` (C#).
 *
 *  ===================== O QUE ESTE ARQUIVO PROTEGE =====================
 *  A mesma regra de minutos úteis existe duas vezes, em linguagens diferentes, porque a cor do
 *  semáforo precisa envelhecer no cliente sem novo fetch. Duas implementações da mesma regra
 *  divergem — não é hipótese, é o que acontece quando alguém mexe em uma só.
 *
 *  E a divergência é difícil de rastrear: o Meu Dia ordena pelo cálculo do SERVIDOR e a caixa
 *  pinta pelo do CLIENTE. A lista "pula" quando o vendedor troca de tela, e não há erro em
 *  lugar nenhum para investigar.
 *
 *  Cada lado com seus próprios casos não pega isso: ficam os dois verdes, discordando. Um
 *  conjunto único, lido pelos dois, pega.
 *  ====================================================================== */
describe('semaforo — paridade com TempoUtil.cs', () => {
  it('carrega os casos do arquivo compartilhado', () => {
    // Se o import silenciosamente virar vazio (arquivo movido, JSON quebrado), todo o `for`
    // abaixo registraria ZERO expectativas e o describe passaria sem provar nada.
    expect(casos.casos.length).toBeGreaterThanOrEqual(10);
  });

  for (const caso of casos.casos) {
    it(`${caso.nome} -> ${caso.esperado}min`, () => {
      const janela: JanelaAtendimento = {
        horaInicio: caso.horaInicio,
        horaFim: caso.horaFim,
        diasSemana: caso.diasSemana,
        feriados: new Set(caso.feriados)
      };

      // `new Date('2026-08-06T19:50:00')` — sem 'Z' e sem offset — é hora LOCAL em JavaScript,
      // que é o mesmo que o C# lê do arquivo (DateTime de Kind Unspecified). É por isso que os
      // casos não trazem zona: com 'Z' os dois lados leriam instantes diferentes em qualquer
      // máquina fora de UTC, e a paridade viraria teste de fuso.
      const obtido = minutosUteis(new Date(caso.inicio), new Date(caso.fim), janela);

      expect(obtido).toBe(caso.esperado);
    });
  }
});
