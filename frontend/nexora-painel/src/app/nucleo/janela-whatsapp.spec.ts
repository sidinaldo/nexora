import { JanelaWhatsapp } from './modelos';
import { situacaoDaJanela } from './janela-whatsapp';

/** A janela de 24h do WhatsApp (INT-XX): o servidor manda os instantes, a tela só compara com o
 *  relógio. O que estes testes seguram é a fronteira entre os três estados e o texto do selo. */
describe('situacaoDaJanela', () => {
  const entrada = new Date('2026-10-09T12:00:00Z');
  const horas = (h: number) => new Date(entrada.getTime() + h * 3_600_000);

  function janela(bloqueia: boolean, comEntrada = true): JanelaWhatsapp {
    return {
      avisoEm: comEntrada ? horas(22).toISOString() : null,
      fechaEm: comEntrada ? horas(24).toISOString() : null,
      bloqueia
    };
  }

  it('sem conversa, não há selo', () => {
    expect(situacaoDaJanela(null, entrada)).toBeNull();
  });

  it('aberta mostra as horas que faltam', () => {
    const s = situacaoDaJanela(janela(true), horas(19))!;
    expect(s.estado).toBe('aberta');
    expect(s.rotulo).toBe('janela 5h');
  });

  it('nas duas últimas horas vira "fechando", com o minuto', () => {
    const s = situacaoDaJanela(janela(true), horas(22.5))!;
    expect(s.estado).toBe('fechando');
    expect(s.rotulo).toBe('fecha em 1h30');

    expect(situacaoDaJanela(janela(true), horas(23.5))!.rotulo).toBe('fecha em 30 min');
  });

  it('no instante em que fecha, está fechada', () => {
    const s = situacaoDaJanela(janela(true), horas(24))!;
    expect(s.estado).toBe('fechada');
    expect(s.rotulo).toBe('janela fechada');
    expect(s.titulo).toContain('só template');
  });

  it('cliente que nunca escreveu: fechada desde o começo', () => {
    const s = situacaoDaJanela(janela(true, false), entrada)!;
    expect(s.estado).toBe('fechada');
    expect(s.titulo).toContain('ainda não escreveu');
  });

  it('a cor vem do estado na API oficial, e na Evolution o selo fica neutro', () => {
    expect(situacaoDaJanela(janela(true), horas(19))!.classe).toBe('selo-ok');
    expect(situacaoDaJanela(janela(true), horas(23))!.classe).toBe('selo-atencao');
    expect(situacaoDaJanela(janela(true), horas(25))!.classe).toBe('selo-perigo');
    expect(situacaoDaJanela(janela(false), horas(25))!.classe).toBe('');
  });

  // ⚠️ Na Evolution o selo informa, não ameaça: dizer "só template" num número que manda texto
  // livre a qualquer hora ensinaria o vendedor a desconfiar do selo.
  it('na Evolution não fala em template e não bloqueia', () => {
    const s = situacaoDaJanela(janela(false), horas(30))!;
    expect(s.estado).toBe('fechada');
    expect(s.bloqueia).toBeFalse();
    expect(s.titulo).not.toContain('template');
    expect(s.titulo).toContain('não bloqueia');
  });
});
