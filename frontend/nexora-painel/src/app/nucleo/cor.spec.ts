import { textoSobre } from './cor';

const BRANCO = '#FFFFFF';
const ESCURO = '#1B2622';

describe('textoSobre', () => {
  it('põe texto escuro sobre fundo claro e branco sobre fundo escuro', () => {
    expect(textoSobre('#FFFFFF')).toBe(ESCURO);
    expect(textoSobre('#FBF7EF')).toBe(ESCURO);   // o creme do projeto
    expect(textoSobre('#000000')).toBe(BRANCO);
    expect(textoSobre('#14432F')).toBe(BRANCO);   // o verde da marca
  });

  it('julga pelo olho, não pela média dos canais', () => {
    // Os três têm o mesmo valor somado. Só o verde é claro o bastante para pedir texto escuro —
    // é isso que os pesos da WCAG dizem, e uma média simples erraria os três.
    expect(textoSobre('#00FF00')).toBe(ESCURO);
    expect(textoSobre('#FF0000')).toBe(BRANCO);
    expect(textoSobre('#0000FF')).toBe(BRANCO);
  });

  it('acerta o amarelo, que é o caso que motivou a função', () => {
    // Amarelo com texto branco é o que se lê pior no chip, e é uma escolha plausível para
    // "Atenção" ou "Aguardando".
    expect(textoSobre('#FFD54F')).toBe(ESCURO);
  });

  it('aceita a forma curta de três dígitos', () => {
    expect(textoSobre('#fff')).toBe(ESCURO);
    expect(textoSobre('#000')).toBe(BRANCO);
  });

  it('não se importa com o "#" nem com espaço em volta', () => {
    expect(textoSobre(' 14432F ')).toBe(BRANCO);
  });

  it('cai no texto escuro quando a cor não dá para ler', () => {
    // Texto escuro sobre o fundo branco do cartão continua legível; branco sumiria.
    for (const ruim of [null, undefined, '', 'vermelho', '#12345', '#GGGGGG']) {
      expect(textoSobre(ruim)).toBe(ESCURO);
    }
  });

  it('cobre toda a faixa de cinza sem deixar buraco', () => {
    // Varredura: não existe cinza em que a função devolva algo fora das duas cores, e a troca
    // acontece uma vez só — função monotônica, sem oscilar no meio.
    let trocas = 0;
    let anterior = textoSobre('#000000');

    for (let c = 1; c <= 255; c++) {
      const hex = '#' + c.toString(16).padStart(2, '0').repeat(3);
      const atual = textoSobre(hex);
      expect([BRANCO, ESCURO]).toContain(atual);
      if (atual !== anterior) { trocas++; anterior = atual; }
    }

    expect(trocas).toBe(1);
  });
});
