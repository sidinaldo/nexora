/** ===================== AS DUAS LETRAS DO AVATAR =====================
 *  Primeira letra do primeiro nome + primeira letra do último. "Wendell Gomes" vira "WG",
 *  "Alexsandro" vira "A".
 *
 *  ⚠️ ESTE ARQUIVO NASCE DE UM PRINT DA CAIXA DE ENTRADA. O contato chamado "(83) 95278-7173"
 *  aparecia com o avatar **"(9"** — o parêntese e o primeiro dígito. Relatado assim: "as iniciais
 *  quando é número fica a chave e o primeiro número".
 *
 *  A causa é a mesma do "Oi, (84)!" na régua, e vale a pena ver junto: contato criado por mensagem
 *  nossa nasce com o telefone formatado como nome (`nome` é NOT NULL, e o telefone é melhor que
 *  vazio), e quem lê o nome assume que ele é um nome. `NomeDePessoa`, no backend, responde à mesma
 *  pergunta para a mensagem que sai; este arquivo responde para a tela.
 *
 *  ⚠️ E ELE CONSERTA UM SEGUNDO CASO QUE NINGUÉM TINHA RELATADO: "Sidinaldo Barbosa 💡" produzia
 *  "S💡", porque o último pedaço do nome era o emoji. Emoji não é letra — e agora não entra.
 *
 *  ⚠️ ERAM SEIS CÓPIAS IDÊNTICAS deste cálculo (`shell`, `caixa`, `dashboard`, `equipe`, `funil`,
 *  `meu-dia`) mais uma sétima, diferente, escrita direto no template de `/contatos`
 *  (`c.nome.charAt(0)`, que dava só "("). Uma cópia só, pelo mesmo motivo que `cor.ts` e
 *  `semaforo.ts` existem: o avatar é a MESMA coisa em toda tela, e o `design-system.spec` compara
 *  `.avatar` entre elas justamente para isso.
 *  ==================================================================== */

/** Qualquer letra, em qualquer alfabeto — `\p{L}` cobre acento, ç e cirílico, e exclui dígito,
 *  pontuação e emoji, que é o ponto. */
const LETRA = /\p{L}/u;

/** O avatar de alguém.
 *
 *  Sem nenhuma letra no nome — o contato que nasceu com o telefone —, cai nos DOIS ÚLTIMOS
 *  DÍGITOS do número.
 *
 *  ⚠️ OS ÚLTIMOS, E NÃO OS PRIMEIROS, e a diferença é o trabalho do avatar: "(83) 95278-7173" e
 *  "(11) 94065-3647" começam igual para quem só vê dois caracteres (era exatamente o print: dois
 *  "(9" indistinguíveis), e terminam em "73" e "47". O avatar existe para separar linhas de
 *  relance; dois iguais não separam nada.
 *
 *  Dígito não se confunde com inicial — ninguém lê "73" como nome —, e o número inteiro está na
 *  linha ao lado, então o "73" se explica sozinho. */
export function iniciais(nome: string | null | undefined): string {
  const texto = (nome ?? '').trim();

  const pedacos = texto.split(/\s+/).filter(p => LETRA.test(p));

  if (pedacos.length > 0) {
    const primeira = pedacos[0].match(LETRA)![0];
    const ultima = pedacos.length > 1 ? pedacos[pedacos.length - 1].match(LETRA)![0] : '';
    return (primeira + ultima).toLocaleUpperCase('pt-BR');
  }

  const digitos = texto.replace(/\D/g, '');
  if (digitos.length >= 2) return digitos.slice(-2);

  // Nem letra nem dois dígitos: nome vazio, ou algo que não dá para resumir. "?" é honesto —
  // melhor que um espaço em branco, que pareceria avatar sem carregar.
  return '?';
}
