/** ===================== TEXTO SOBRE COR ESCOLHIDA PELO CLIENTE =====================
 *  Em todo o resto do painel a cor do cliente aparece numa bolinha — etapa do funil, fatia de
 *  gráfico, ponto de etapa —, e bolinha não tem texto em cima. A etiqueta é o primeiro lugar em
 *  que o texto fica DENTRO da cor.
 *
 *  Isso quebra a regra do `styles.css` de que par de cores é decidido na paleta: aqui o fundo é
 *  escolhido no seletor do sistema operacional, e amarelo claro com texto branco é ilegível.
 *  Fixar `color: #FFF` no CSS entregaria uma etiqueta que só se lê se a cor for escura.
 *
 *  A conta é a luminância relativa da WCAG, e o corte em 0,5 é o usual: acima disso o fundo é
 *  claro e pede o texto escuro do projeto; abaixo, o branco.
 *  ================================================================================= */

const CLARO = '#FFFFFF';
/** O mesmo `--texto` do `styles.css`. Repetido aqui porque isto é cálculo, não folha de estilo —
 *  e `var(--texto)` não sobrevive a uma comparação numérica. */
const ESCURO = '#1B2622';

/** Devolve a cor de texto legível sobre `fundo` (`#RGB` ou `#RRGGBB`).
 *  Cor que não dá para ler cai no texto escuro, que é o do corpo da página. */
export function textoSobre(fundo: string | null | undefined): string {
  const rgb = paraRgb(fundo);
  if (!rgb) return ESCURO;

  return luminancia(rgb) > 0.5 ? ESCURO : CLARO;
}

function paraRgb(hex: string | null | undefined): [number, number, number] | null {
  const limpo = (hex ?? '').trim().replace(/^#/, '');

  // `#abc` é a forma curta: cada dígito vale por dois. O seletor nativo nunca devolve assim,
  // mas a cor entra por API e pode ter sido gravada à mão.
  const cheio = limpo.length === 3 ? limpo.split('').map(d => d + d).join('') : limpo;
  if (!/^[0-9a-fA-F]{6}$/.test(cheio)) return null;

  return [
    parseInt(cheio.substring(0, 2), 16),
    parseInt(cheio.substring(2, 4), 16),
    parseInt(cheio.substring(4, 6), 16)
  ];
}

/** Luminância relativa da WCAG 2.x, em 0..1.
 *
 *  Os pesos (0,2126 / 0,7152 / 0,0722) são o olho humano, não uma média: verde pesa dez vezes
 *  mais que azul, e é por isso que `#00FF00` pede texto escuro e `#0000FF` pede branco. */
function luminancia([r, g, b]: [number, number, number]): number {
  const [lr, lg, lb] = [r, g, b].map(c => {
    const v = c / 255;
    // A correção de gama do sRGB. Sem ela, meio-tom é julgado claro demais.
    return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4);
  });

  return 0.2126 * lr + 0.7152 * lg + 0.0722 * lb;
}
