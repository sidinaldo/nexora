import { iniciais } from './iniciais';

/** AS DUAS LETRAS DO AVATAR.
 *
 *  ⚠️ ESTA SUÍTE NASCE DE UM PRINT DA CAIXA: o contato "(83) 95278-7173" aparecia com o avatar
 *  "(9" — o parêntese e o primeiro dígito. Relatado assim: "as iniciais quando é número fica a
 *  chave e o primeiro número".
 *
 *  Teste puro, sem TestBed: a regra é de texto e vale para as sete telas que a usavam em cópia. */
describe('iniciais — o avatar de alguém', () => {
  it('PEGA A PRIMEIRA E A ÚLTIMA, como sempre pegou', () => {
    expect(iniciais('Wendell Gomes')).toBe('WG');
    expect(iniciais('Alexsandro')).toBe('A');
    expect(iniciais('Terezinha')).toBe('T');
    expect(iniciais('  Maria   da   Silva  ')).toBe('MS');   // o do meio não conta
    expect(iniciais('ana paula')).toBe('AP');                // caixa alta sempre
  });

  it('ACENTO E Ç SÃO LETRAS', () => {
    expect(iniciais('Ícaro Çelik')).toBe('ÍÇ');
    expect(iniciais('Ângela')).toBe('Â');
  });

  /** ⚠️ O CASO DO PRINT. O contato criado por mensagem NOSSA nasce com o telefone formatado como
   *  nome — `nome` é NOT NULL, e o telefone é melhor que vazio —, e quem lia o nome assumia que
   *  ele era um nome. */
  it('NÚMERO NÃO VIRA INICIAL: cai nos dois últimos dígitos', () => {
    expect(iniciais('(83) 95278-7173')).toBe('73');
    expect(iniciais('(11) 94065-3647')).toBe('47');
    expect(iniciais('5584988887777')).toBe('77');
    expect(iniciais('+55 84 95278-7173')).toBe('73');
  });

  /** ⚠️ OS ÚLTIMOS DÍGITOS, E NÃO OS PRIMEIROS, porque o avatar existe para SEPARAR linhas. No
   *  print os dois contatos mostravam "(9" — o mesmo avatar para pessoas diferentes, que é o
   *  avatar não fazendo o trabalho dele. */
  it('DOIS NÚMEROS DIFERENTES NÃO GANHAM O MESMO AVATAR', () => {
    expect(iniciais('(83) 95278-7173')).not.toBe(iniciais('(11) 94065-3647'));
  });

  /** ⚠️ ACHADO DE CARONA, que ninguém tinha relatado: o último pedaço do nome era um emoji, e o
   *  avatar virava "S💡". Emoji não é letra. */
  it('EMOJI NÃO É LETRA', () => {
    expect(iniciais('Sidinaldo Barbosa 💡')).toBe('SB');
    expect(iniciais('💡')).toBe('?');
    expect(iniciais('João 🏐🤩🩷')).toBe('J');
  });

  it('SEM NADA APROVEITÁVEL, "?" — e nunca vazio', () => {
    expect(iniciais('')).toBe('?');
    expect(iniciais('   ')).toBe('?');
    expect(iniciais(null)).toBe('?');
    expect(iniciais(undefined)).toBe('?');
    expect(iniciais('#')).toBe('?');
    expect(iniciais('7')).toBe('?');       // um dígito só não faz par
  });

  it('NUNCA DEVOLVE MAIS QUE DOIS CARACTERES', () => {
    for (const n of ['Wendell Gomes', '(83) 95278-7173', 'Maria da Silva Santos', '💡', '']) {
      expect(iniciais(n).length).withContext(`"${n}"`).toBeLessThanOrEqual(2);
    }
  });
});
