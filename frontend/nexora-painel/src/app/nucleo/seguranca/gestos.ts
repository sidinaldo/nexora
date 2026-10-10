import { Permissao } from '../modelos';

/** ===================== O GRUPO "CONFIGURAÇÃO" DO MENU =====================
 *  Os gestos que fazem o grupo existir. Cada LINK dentro dele pede o seu próprio gesto; esta
 *  lista responde a outra pergunta — "há algum item para mostrar?" —, e é ela que decide se o
 *  separador aparece.
 *
 *  ⚠️ ANTES O GRUPO INTEIRO ERA `configurar_empresa`, e isso deixou de funcionar no PER-1. Aquele
 *  gesto se partiu em cinco (conexão, etiquetas, captação, funis, anúncios) e ficou só com o
 *  resíduo — dados da empresa, janela, feriados e o webhook de saída. Mantê-lo como invólucro
 *  esconderia Etiquetas de quem recebeu Etiquetas: a pessoa teria o gesto, a rota abriria, e o
 *  link não estaria em lugar nenhum.
 *
 *  ⚠️ UMA LISTA, DOIS MENUS. A barra lateral e a tela "Mais" (o menu do celular, MOB-2) mostram o
 *  mesmo grupo; escrita à mão nos dois, ela divergiria no primeiro gesto novo — e o jeito de
 *  descobrir seria um cliente dizendo que a tela existe no computador e não no telefone.
 *  ========================================================================== */
/** Um gesto que o dono liga e desliga por pessoa, com o texto que a tela mostra. */
export interface GestoDelegavel {
  chave: Permissao;
  rotulo: string;
  /** ⚠️ APARECE NA LINHA, não atrás de um ⓘ. `anonimizar_contato` é irreversível e
   *  `cancelar_venda` mexe em dinheiro: esconder a consequência atrás de um hover é a escolha
   *  errada quando quem lê está decidindo. */
  descricao: string;
  grupo: 'dia' | 'configuracao';
}

/** ===================== OS DEZ QUE SE DELEGAM =====================
 *  A mesma lista de `Permissoes.Delegaveis` no servidor, que é quem decide de verdade — aqui ela
 *  existe para a tela ter ROTULO e DESCRIÇÃO, que o servidor não tem por que saber.
 *
 *  ⚠️ OS DOIS QUE FALTAM FALTAM DE PROPÓSITO: `gerenciar_equipe` (quem muda papéis promove um
 *  colega a Dono e pede o favor de volta) e `configurar_empresa` (ficou com o webhook de saída,
 *  que manda a base de contatos para qualquer URL). Pôr um deles aqui não abriria acesso —
 *  `Permissoes.Pode` ignora exceção em gesto indelegável e o `ServicoEquipe` recusa gravar —, mas
 *  colocaria na tela um interruptor que não faz nada.
 *
 *  ⚠️ ESTA LISTA E A DO SERVIDOR NÃO SE CONFEREM SOZINHAS, igual ao `PERMISSOES_DE` dos testes. O
 *  que segura cada lado é um teste: `OS_DEZ_GESTOS_DELEGAVEIS_SAO_ESTES` no backend e
 *  `TODO GESTO DELEGÁVEL TEM RÓTULO E DESCRIÇÃO` aqui.
 *  ================================================================= */
export const GESTOS_DELEGAVEIS: GestoDelegavel[] = [
  // ---- o dia a dia ----
  {
    chave: 'importar_contatos', grupo: 'dia',
    rotulo: 'Importar contatos',
    descricao: 'Sobe planilha de contatos para a empresa.'
  },
  {
    chave: 'cancelar_venda', grupo: 'dia',
    rotulo: 'Cancelar venda',
    descricao: 'Tira o valor do faturamento. Toda troca fica no histórico.'
  },
  {
    chave: 'ver_historico', grupo: 'dia',
    rotulo: 'Ver histórico',
    descricao: 'A trilha de quem mexeu em quê.'
  },
  {
    chave: 'anonimizar_contato', grupo: 'dia',
    rotulo: 'Anonimizar contato',
    descricao: 'LGPD. Apaga o nome do contato para sempre, sem volta.'
  },
  {
    chave: 'ver_numeros_da_equipe', grupo: 'dia',
    rotulo: 'Ver números da equipe',
    descricao: 'Relatórios de todo mundo. Sem isso, cada um vê só o seu.'
  },
  {
    chave: 'agir_em_lote', grupo: 'dia',
    rotulo: 'Agir em lote',
    descricao: 'Criar lembrete, etiquetar e reabrir vários leads parados de uma vez. '
      + 'Sem isso, a pessoa vê a lista e age um por um.'
  },

  // ---- a configuração, que saiu de `configurar_empresa` ----
  {
    chave: 'gerenciar_conexao', grupo: 'configuracao',
    rotulo: 'WhatsApp',
    descricao: 'Adiciona e conecta o número de WhatsApp da empresa.'
  },
  {
    chave: 'gerenciar_etiquetas', grupo: 'configuracao',
    rotulo: 'Etiquetas',
    descricao: 'Cria, edita e remove etiquetas.'
  },
  {
    chave: 'gerenciar_captacao', grupo: 'configuracao',
    rotulo: 'Captação',
    descricao: 'Formulários do site e o link/QR de captação.'
  },
  {
    chave: 'gerenciar_funis', grupo: 'configuracao',
    rotulo: 'Funis e etapas',
    descricao: 'Cria e edita funis. Funil com negociações não se apaga.'
  },
  {
    chave: 'gerenciar_anuncios', grupo: 'configuracao',
    rotulo: 'Anúncios',
    descricao: 'Credencial de conversão da Meta. Não inclui webhook de saída.'
  }
];

export const GESTOS_DE_CONFIGURACAO: Permissao[] = [
  'gerenciar_equipe',
  'gerenciar_conexao',
  'gerenciar_etiquetas',
  'gerenciar_captacao',
  'gerenciar_funis',
  'gerenciar_anuncios',
  'configurar_empresa'
];
