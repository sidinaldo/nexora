/** Contratos que a API devolve. Escritos a partir do que os blocos 1–4 expõem — o
 *  `modelos.ts` do Recupera tem 941 linhas de DTO de cobrança e não serve.
 *
 *  Enum sempre como TEXTO (a API serializa com JsonStringEnumConverter): assim a ordem
 *  do enum em C# não vira uma duplicata implícita aqui, que quebraria em silêncio se
 *  alguém inserisse um valor no meio. */

export type PapelUsuario = 'dono' | 'gestor' | 'vendedor';
export type StatusUsuario = 'ativo' | 'convidado' | 'inativo';
export type DirecaoMensagem = 'entrada' | 'saida';
export type StatusConversa = 'aberta' | 'resolvida';
export type TipoMidia = 'nenhum' | 'imagem' | 'documento' | 'audio' | 'video';
export type StatusConexao = 'nao_criada' | 'conectando' | 'conectado' | 'desconectado' | 'offline';

export type FiltroConversa = 'Aguardando' | 'Minhas' | 'NaoAtribuidas' | 'Todas' | 'Resolvidas';

// ---------------------------------------------------------------- auth
export interface UsuarioAutenticado {
  id: number;
  nome: string;
  email: string;
  /** Para MOSTRAR ("dono", "gestor"). Nunca para decidir o que oferecer — ver `permissoes`. */
  papel: PapelUsuario;
  empresaId: number;
  empresaNome: string;
  /** O que esta pessoa pode fazer, decidido pelo SERVIDOR (`Seguranca.Permissoes`). É por aqui
   *  que a tela escolhe o que oferecer: `auth.pode('importar_contatos')`.
   *
   *  ⚠️ OPCIONAL porque a sessão aberta antes desta lista existir a guardou sem ela. Ausente é
   *  "ainda não sei", e o `AuthServico` pergunta ao servidor — nunca deduz do papel. */
  permissoes?: Permissao[];
}

/** Os gestos, com os nomes da API. A mesma lista de `PermissoesTests.OS_NOMES_QUE_O_PAINEL_LE`:
 *  um nome trocado lá esconderia o botão aqui sem erro nenhum. */
export type Permissao =
  // Os dois que NÃO se delegam por pessoa.
  | 'configurar_empresa'
  | 'gerenciar_equipe'
  // Os cinco que saíram de `configurar_empresa` no PER-1.
  | 'gerenciar_conexao'
  | 'gerenciar_etiquetas'
  | 'gerenciar_captacao'
  | 'gerenciar_funis'
  | 'gerenciar_anuncios'
  // Os cinco de operação.
  | 'importar_contatos'
  | 'cancelar_venda'
  | 'ver_historico'
  | 'anonimizar_contato'
  | 'ver_numeros_da_equipe'
  // Agir em lote — nasceu com a tela de leads parados (LPA-1).
  | 'agir_em_lote';

export interface LoginResponse {
  token: string;
  expiraEm: string;
  usuario: UsuarioAutenticado;
}

/** Formato de erro da API: o FiltroRegraDeNegocio devolve sempre { erro: "..." },
 *  com mensagem já em português e voltada ao usuário final. */
export interface ErroApi {
  erro: string;
}

// ---------------------------------------------------------------- paginação
/** Página por CURSOR (não por offset). A lista da caixa se reordena em tempo real —
 *  com offset, a página seguinte pula ou repete linha. */
export interface PaginaCursor<T> {
  itens: T[];
  temMais: boolean;
}

/** Página por OFFSET, com total. Usada onde a lista NÃO se reordena sozinha (contatos, que é
 *  ordenada por nome) — e onde o total importa para mostrar "142 contatos". */
export interface Pagina<T> {
  total: number;
  numeroPagina: number;
  tamanho: number;
  itens: T[];
}

// ---------------------------------------------------------------- contatos e funil
/** ⚠️ `meta_ads` ENTROU NO SERVIDOR (INT-XX) E ESTE TIPO FICOU PARA TRÁS. O contato importado do
 *  Gerenciador de Leads vinha com uma origem que o painel não conhecia: sumia do filtro de origem
 *  e caía fora do seletor ao editar o contato — sem erro nenhum, porque enum que chega como texto
 *  não estoura, só não casa. */
export type OrigemLead =
  | 'instagram' | 'facebook' | 'whatsapp' | 'google'
  | 'site' | 'qrcode' | 'indicacao' | 'meta_ads' | 'manual' | 'outro';

export type FiltroContato = 'Abertos' | 'Ganhos' | 'Perdidos' | 'Todos';

/** Quantos contatos há em CADA aba, com a busca e os demais recortes já aplicados.
 *
 *  ⚠️ EXISTE PORQUE A ABA SOZINHA ESCONDIA GENTE. Relatado assim: "fechei os cards da Ysia em
 *  todos os funis e o contato sumiu da lista". Não tinha sumido — estava em "Ganhos", uma aba ao
 *  lado, e nada na tela apontava para lá.
 *
 *  `abertos + ganhos + perdidos === todos`, sempre. Os quatro números ficam lado a lado na tela,
 *  então um buraco aparece somado. */
export interface ContagemPorSituacao {
  abertos: number;
  ganhos: number;
  perdidos: number;
  todos: number;
}

/** A página da lista de contatos. É uma `Pagina<ContatoResumo>` com as contagens junto — os
 *  quatro primeiros campos são os mesmos, de propósito. */
export interface PaginaContatos extends Pagina<ContatoResumo> {
  contagens: ContagemPorSituacao;
}

/** Um negócio vivo, do tamanho de um chip. Versão leve de `NegocioDoContato`: sem `versao` e
 *  sem etiquetas, que só a tela do contato usa (para mover e etiquetar). */
export interface NegocioNaLista {
  id: number;
  pipelineId: number;
  pipelineNome: string;
  etapaId: number;
  etapaNome: string;
  status: 'aberta' | 'ganha';
  valor: number | null;
}

// ⚠️ `LinhaImportada` E `ResumoImportacao` SAÍRAM: eram o contrato da importação da issue #8, cuja
// tela foi absorvida pela de `/importar` (INT-XX). As rotas continuam de pé no servidor, sem tela;
// tipo sem uso aqui é código morto com uma regra dentro, e foi assim que uma cópia errada da
// "situação" sobreviveu meses.

/** A caixinha "Avisar minhas integrações", DECIDIDA NO SERVIDOR (`AvisoIntegracoes` no backend).
 *  `disponivel` = há webhook ativo ouvindo `lead.criado`; `marcadoPorPadrao` = como ela chega.
 *  A tela não deduz nenhum dos dois — só desenha. */
export interface AvisoIntegracoes {
  disponivel: boolean;
  marcadoPorPadrao: boolean;
}

export interface ContatoResumo {
  id: number;
  nome: string;
  telefone: string;
  email: string | null;
  origem: OrigemLead;
  /** ⚠️ NULOS desde o E6: contato sem negociação não está em funil nenhum. */
  etapaId: number | null;
  /** ⚠️ A LISTA NÃO USA MAIS — ver `negocios`. Uma resposta para uma pergunta que passou a ter
   *  várias. */
  etapaNome: string | null;
  /** ONDE esta pessoa está: um item por negócio vivo (`aberta` ou `ganha`).
   *
   *  ⚠️ A COLUNA "ETAPA" MOSTRAVA UMA ETAPA SÓ, escolhida pelo maior id entre as abertas.
   *  Relatado assim: "por que na lista de contato Ysia ficou com a etiqueta de impedimento? esse
   *  contato está em 3 funil diferente". "Impedimento" era uma ETAPA, do funil Teste, e ganhou a
   *  disputa por ter nascido por último. */
  negocios: NegocioNaLista[];
  ordemKanban: number;
  responsavelId: number | null;
  responsavelNome: string | null;
  valor: number | null;
  ganhoEm: string | null;
  perdidoEm: string | null;
  /** O selo, DECIDIDO NO SERVIDOR com as mesmas expressões das abas. A tela não o recalcula —
   *  duas cópias da regra já divergiram (ver `RegrasNegociacao.Situacao`). */
  situacao: SituacaoContato;
  criadoEm: string;
  conversaId: number | null;
  aguardandoDesde: string | null;
  naoLidas: number;
}

/** `sem_negocio` e `aberto` são as duas metades da aba "Em aberto"; os outros, as abas homônimas. */
export type SituacaoContato = 'sem_negocio' | 'aberto' | 'ganho' | 'perdido';

// ==================================================================== importar leads (INT-XX)
/** Para onde uma coluna do arquivo vai. `ignorar` é o padrão do que ninguém reconheceu — e é uma
 *  resposta legítima: a planilha do cliente tem colunas que não são do Nexora. */
export type CampoImportacao =
  | 'ignorar' | 'nome' | 'telefone' | 'email' | 'observacoes' | 'origem_detalhe' | 'origem'
  | 'meta_lead_id' | 'meta_ad_id' | 'meta_campaign_id' | 'meta_form_id' | 'criado_em';

export interface ColunaMapeada {
  /** O nome COMO O CLIENTE ESCREVEU no cabeçalho. */
  coluna: string;
  campo: CampoImportacao;
}

/** O upload: o arquivo foi aceito e guardado. Nada virou contato. */
export interface ImportacaoRecebida {
  id: number;
  nomeArquivo: string;
  totalLinhas: number;
  /** Uma entrada por coluna, na ordem do arquivo — as reconhecidas já ligadas. */
  mapeamento: ColunaMapeada[];
  /** De onde o arquivo PARECE vir, para a pergunta da tela já chegar respondida: export da Meta
   *  → `meta_ads`; planilha do cliente → `manual`. Quem confirma é o dono. */
  origemSugerida: OrigemLead;
}

/** Uma linha já transformada: o que ela VAI virar. `telefone` é o normalizado. */
export interface LinhaPrevia {
  linha: number;
  nome: string;
  telefone: string | null;
  email: string | null;
  metaLeadId: string | null;
  criadoEm: string | null;
  resultado: 'importado' | 'duplicado' | 'invalido';
  motivo: string | null;
}

export interface PreviaImportacao {
  total: number;
  novos: number;
  duplicados: number;
  invalidos: number;
  /** As 10 primeiras — os totais acima contam o arquivo inteiro. */
  primeiras: LinhaPrevia[];
  /** A caixinha "Avisar minhas integrações", decidida pelo servidor. */
  aviso: AvisoIntegracoes;
}

/** O que o dono escolheu antes de mandar gravar. */
export interface GravarImportacao {
  mapeamento: ColunaMapeada[];
  pipelineId: number | null;
  responsavelId: number | null;
  avisarIntegracoes: boolean;
  /** De onde vieram — o canal. Uma coluna mapeada como `origem` manda por linha; esta vale para o
   *  resto. Sem ela, todo contato importado entraria como lead de anúncio. */
  origem: OrigemLead;
}

export interface ResultadoImportacao {
  id: number;
  total: number;
  importados: number;
  duplicados: number;
  invalidos: number;
  /** `processando` = o arquivo é grande e quem termina é o job; a tela pergunta de novo. */
  status: 'aguardando_mapeamento' | 'processando' | 'concluida' | 'erro';
}

/** O card do kanban. Projeção mais enxuta que a da lista: o quadro carrega dezenas por coluna,
 *  e cada campo a mais é multiplicado pelo número de cards na tela.
 *
 *  ⚠️ ELE DEIXOU DE SE CHAMAR `CardFunil` PORQUE DEIXOU DE SER O CONTATO (E4c/2). `id` é o id
 *  da NEGOCIAÇÃO, e a mesma pessoa pode ter dois cards — quem ganhou e reabriu tem o pedido
 *  pendente numa coluna e a negociação nova em outra.
 *
 *  Manter o nome antigo com o significado novo seria a pior combinação: quem lesse `card.id` e
 *  passasse esse número para uma API de contato escreveria um bug que compila. */
export interface CardFunil {
  /** O id da NEGOCIAÇÃO — é ele que vai no arrasto. */
  id: number;
  /** O id do CONTATO. Tudo que é da PESSOA vai por aqui: abrir o contato, etiquetar, registrar a
   *  venda, carregar os canais do fechamento. */
  contatoId: number;
  nome: string;
  telefone: string;
  ordemKanban: number;
  valor: number | null;
  /** POS-1 · este negócio já foi vendido. É o que diz se `valor` é estimativa ou dinheiro.
   *
   *  ⚠️ Manda nos CONTROLES DO CARD — "Concluir" em vez de "Registrar venda", e a caixa de
   *  seleção. Antes isso saía da coluna (`col.eGanho`), e com etapas depois da venda a coluna
   *  deixou de responder a pergunta: uma coluna de pós-venda pode conter um card em aberto. */
  ganha: boolean;
  responsavelId: number | null;
  responsavelNome: string | null;
  conversaId: number | null;
  aguardandoDesde: string | null;
  naoLidas: number;
  ultimaMensagemEm: string | null;
  /** NEG-3 · a campanha detectada neste ciclo, ou null. Responde "por que este lead está aqui"
   *  sem abrir o card. */
  canalDoCiclo: string | null;
  /** `xmin` da NEGOCIAÇÃO. Volta ao servidor no arrasto; se outra pessoa mexeu no card no meio
   *  do caminho, a API recusa com 409 e a coluna é recarregada. */
  versao: number;

  /** As etiquetas deste contato. ⚠️ O card CORTA no que couber numa linha — o quadro perde valor
   *  se cada card crescer porque alguém marcou oito. */
  etiquetas: EtiquetaDto[];
}

/** Uma linha da lista de negócios do contato.
 *
 *  ⚠️ A LISTA EXISTE PORQUE A TELA MOSTRAVA UM SÓ. O bloco "Negociação" tinha UM seletor de
 *  etapa e UMA situação — escrito quando um contato era um card. Com a mesma pessoa em Vendas e
 *  em Pós-venda não existe resposta para "qual etapa o select mostra" nem "qual negócio ele
 *  move", e a segunda simplesmente sumia da tela. */
export interface NegocioDoContato {
  /** O id da NEGOCIAÇÃO — é ele que vai no mover e no marcar etiqueta. */
  id: number;
  pipelineId: number;
  pipelineNome: string;
  etapaId: number;
  etapaNome: string;
  valor: number | null;
  status: 'aberta' | 'ganha';
  ganhaEm: string | null;
  /** O `xmin` do card. Vai no mover para detectar que outra pessoa mexeu no meio. */
  versao: number;
  etiquetas: EtiquetaDto[];
}

export interface ContatoDetalhe {
  contato: ContatoResumo;
  /** Em qual FUNIL o contato está. O seletor de etapa lista as etapas DESTE funil — pedir "um
   *  funil qualquer" enchia o combo com etapas de outra empresa, ou com nada. */
  /** ⚠️ NULO quando não há negociação (E6): sem funil não há etapas para o seletor da tela
   *  listar, e ele simplesmente não aparece. */
  pipelineId: number | null;
  /** Os negócios VIVOS desta pessoa — um por funil onde ela está. Ver `NegocioDoContato`. */
  negocios: NegocioDoContato[];
  origemDetalhe: string | null;
  observacoes: string | null;
  motivoPerda: string | null;
  anonimizadoEm: string | null;
  ultimaMensagemEm: string | null;
  /** NEG-3 · a campanha que trouxe o cliente DE VOLTA, se houver. Não confundir com
   *  `origemDetalhe`, que é a do cadastro original e fica congelada. */
  canalDoCiclo: string | null;
  lembretes: LembreteDto[];
  /** Onde "Abrir negociação" pode dar certo — o mesmo cálculo da caixa, feito no servidor. */
  funisDisponiveis: FunilLivre[];
  /** De onde esta pessoa veio (INT-4). NULO para a maioria — e aí a tela não mostra o bloco. */
  jornada: JornadaDoAnuncio | null;
}

/** A JORNADA: o clique que trouxe a pessoa, e o que a Meta ficou sabendo (INT-4).
 *
 *  ⚠️ SEM IP E SEM USER-AGENT, de propósito. Eles existem para a Meta casar quem clicou com quem
 *  virou lead; na tela não servem para nada, e mostrá-los seria expor dado pessoal de alguém que
 *  nem é cliente a todo usuário do tenant. O identificador de clique também não vem cru — vem como
 *  `deAnuncioPago`, que é a única pergunta que a tela faz. */
export interface JornadaDoAnuncio {
  fonte: string;
  utmSource: string | null;
  utmMedium: string | null;
  utmCampaign: string | null;
  utmContent: string | null;
  utmTerm: string | null;
  pagina: string | null;
  referencia: string | null;
  deAnuncioPago: boolean;
  ocorridoEm: string;
  eventos: EventoDaJornada[];
}

export interface EventoDaJornada {
  /** `lead` ou `compra`. */
  tipo: string;
  status: string;
  entregueEm: string | null;
  erro: string | null;
}

/** Um funil onde a pessoa ainda não tem card. A regra de "ocupado" (`aberta` ou `ganha`) é do
 *  servidor (`RegrasNegociacao.OcupaOFunil`); a tela só lista. */
export interface FunilLivre {
  id: number;
  nome: string;
}

export interface ColunaFunil {
  etapaId: number;
  nome: string;
  ordem: number;
  cor: string;
  eGanho: boolean;
  /** POS-1 · esta etapa vem DEPOIS da etapa de ganho ("Pós-Venda", "Entregue"). Calculado no
   *  servidor, que é quem sabe a ordem da etapa de ganho.
   *
   *  Manda no ENFEITE DA COLUNA: o selo, aceitar ou recusar o drop, o texto do valor. Para o
   *  controle de um CARD, use `card.ganha`. */
  posGanho: boolean;
  /** Do conjunto INTEIRO da coluna, não da página carregada. */
  total: number;
  valorTotal: number;
  /** Quantas vendas já foram CONCLUÍDAS nesta etapa (NEG-2, estendido no POS-1). Conta na etapa
   *  de ganho E nas de pós-venda, porque concluir a partir de "Entregue" cai na contagem daquela
   *  etapa. Sem esse segundo número, a coluna esvaziando pareceria perda de dado. */
  concluidas: number;
  contatos: CardFunil[];
  temMais: boolean;
}

export interface QuadroFunil {
  colunas: ColunaFunil[];
}

/** Os números do cabeçalho de UMA coluna, contados no servidor sobre a coluna inteira (AUD-XX). */
export interface TotaisColuna {
  etapaId: number;
  total: number;
  valorTotal: number;
  concluidas: number;
}

/** Uma página de cards de uma coluna, com os números do cabeçalho dela (AUD-XX). */
export interface PaginaColuna extends PaginaCursor<CardFunil> {
  total: number;
  valorTotal: number;
  concluidas: number;
}

/** O que o arrasto devolve: a ordem nova do card e os números das colunas que ele mexeu (AUD-XX). */
export interface ResultadoMover {
  ordemKanban: number;
  colunas: TotaisColuna[];
}

// ---------------------------------------------------------------- pipelines
/** Uma pipeline. A empresa tem várias, cada uma com as SUAS etapas. */
export interface PipelineDto {
  id: number;
  nome: string;
  cor: string;
  ordem: number;
  /** Onde o lead entra quando nada mais decide. Exatamente uma por empresa. */
  padrao: boolean;
  etapas: number;
  /** Negócios ABERTOS — o número ao lado do nome no menu.
   *
   *  ⚠️ É a mesma conta que o quadro soma nas colunas, e isso não é coincidência: o servidor usa
   *  a `Expression` compartilhada de visibilidade, e `PipelinesDbTests` exige que os dois valores
   *  batam. Essa regra já divergiu uma vez entre quadro e dashboard, e o cliente viu 72 numa
   *  etapa onde havia 69 cards. */
  contatos: number;
}

// ---------------------------------------------------------------- painel
export interface StatusPainel {
  naoLidas: number;
  aguardando: number;
  /** Quantos follow-ups ESTE usuário tem vencidos ou para hoje — o número do contador ao lado do
   *  "Meu Dia" no menu.
   *
   *  ⚠️ Vem neste payload, e não de rota própria: o shell já bate `/painel/status` a cada 45s. */
  lembretesHoje: number;
  /** false quando ALGUMA conexão já pareada está fora do ar — não quando todas estão.
   *  Com dois números, esperar os dois caírem significa deixar o vendedor digitar resposta
   *  num número morto enquanto o painel diz que está tudo bem. */
  whatsappConectado: boolean;
  /** Os NOMES das conexões caídas, para o banner dizer qual. Vazio quando está tudo no ar. */
  conexoesCaidas: string[];
  trocouDeNumero: boolean;
  /** Limites do semáforo, em minutos. Vêm do servidor, mas quem PINTA é o cliente:
   *  a cor envelhece entre requisições e a lista precisa amadurecer sozinha. */
  semaforoAmareloMinutos: number;
  semaforoVermelhoMinutos: number;
  /** A janela de atendimento da empresa. Vem junto porque a cor NÃO PODE acender fora do
   *  expediente — sem ela o cliente contaria a madrugada como espera e tudo amanheceria
   *  vermelho. `janelaDiasSemana` é bitmask: bit 0 = domingo … bit 6 = sábado. */
  janelaHoraInicio: number;
  janelaHoraFim: number;
  janelaDiasSemana: number;
  /** Feriados dos últimos 30 dias ('YYYY-MM-DD'). O navegador não tem como saber que a
   *  terça-feira foi feriado, e sem isso o desconto do tempo útil erra o dia inteiro. */
  feriadosRecentes: string[];
  /** Mensagens que entraram atrasadas nas últimas 24h, por queda do WhatsApp ou da API.
   *  `null` no caso normal. Vem no poll de 45s para o aviso aparecer sozinho — sem isso o
   *  vendedor só descobriria recarregando a página que dez conversas mudaram debaixo dele. */
  recuperacao: AvisoRecuperacao | null;
}

// ---------------------------------------------------------------- meu dia
export type TipoAcao = 'responder' | 'lembrete';

/** Uma linha do plano do dia. `aguardandoDesde` e `minutosUteis` vêm JUNTOS de propósito:
 *  o timestamp para o cliente pintar a cor (que envelhece sozinha) e os minutos ÚTEIS já
 *  descontados do que estava fora do expediente — desconto que depende dos feriados, e o
 *  navegador não os tem sem pedir. */
export interface AcaoDoDia {
  tipo: TipoAcao;
  id: number;
  contatoId: number;
  contatoNome: string;
  telefone: string;
  titulo: string;
  conversaId: number | null;
  aguardandoDesde: string | null;
  minutosUteis: number | null;
  /** Espera mais velha que a janela de feriados carregada: `minutosUteis` vem nulo de propósito,
   *  porque o número sairia sem descontar feriados antigos — maior que o real e com cara de
   *  exato. A tela mostra "mais de 30 dias". */
  esperaAcimaDaJanela: boolean;
  horaAlvo: string | null;
  dataAlvo: string | null;
  atrasado: boolean;
}

export interface MeuDia {
  acoes: AcaoDoDia[];
  respondendo: number;
  lembretes: number;
}

/** Quantas ações cada aba do Meu Dia tem — do dia INTEIRO, contadas no servidor (AUD-XX). */
export interface ContagemDoDia {
  todas: number;
  responder: number;
  lembrete: number;
  atrasadas: number;
}

/** Uma página de uma aba do Meu Dia, já na ordem do dia, com as contagens de todas (AUD-XX). */
export interface PaginaDoDia {
  itens: AcaoDoDia[];
  contagens: ContagemDoDia;
  totalCount: number;
  pagina: number;
  tamanhoPagina: number;
  totalPaginas: number;
}

// ---------------------------------------------------------------- dashboard
/** Uma linha da visão macro do painel: um funil, e como ele está.
 *
 *  ⚠️ ERA `EtapaFunilDto`, uma lista com as etapas de TODOS os funis juntas. Com dois funis a tela
 *  mostrava "Novo Lead" e "Novo lead" coladas, sem dizer de quem era qual — e pior: `ordem` é única
 *  POR FUNIL, então as duas etapas de ordem 1 saíam lado a lado e a forma de funil desaparecia.
 *
 *  ⚠️ DOIS RECORTES DE TEMPO NA MESMA LINHA. `emNegociacao`/`valorEmAberto` são AGORA;
 *  `ganhasNoMes`/`conversao` são DO MÊS. A tela TEM que dizer qual é qual em cada coluna: sem isso
 *  o dono compara o total daqui com "Vendas do mês" lá em cima e conclui que os números não batem. */
export interface FunilNoPainelDto {
  pipelineId: number;
  nome: string;
  cor: string;
  emNegociacao: number;
  valorEmAberto: number;
  ganhasNoMes: number;
  /** A MESMA conta do KPI do topo: ganhas ÷ (ganhas + perdidas) no mês — já pronta do servidor.
   *
   *  ⚠️ A TELA FORMATA, NÃO RECALCULA, e usa o MESMO `percentual()` do KPI. Refazer a conta aqui,
   *  mesmo "igualzinho", é como as duas versões divergem: o desenho avaliado no FUN-1 trazia
   *  conversão como "ganhas ÷ entradas", e duas fórmulas com o mesmo nome na mesma tela quebram na
   *  primeira conferência que o dono fizer.
   *
   *  De 0 a 100 com 2 casas, e null sem nada decidido no mês ("—", e não "0%") — AUD-XX. */
  conversaoPercentual: number | null;
}

// Os tipos do modo demonstração fictício (IndicadorDemo, EtapaFunilDemo, OrigemDemo,
// AtividadeDemo, TarefaDemo, PontoSerieDemo, DashboardDemo) foram REMOVIDOS junto com
// `/api/dashboard/demo`. A demonstração agora é um tenant com dados reais — ver docs/PI-4b.md.

// ---------------------------------------------------------------- série temporal (REAL)
export type AgrupamentoSerie = 'dia' | 'semana' | 'mes';

/** Um ponto da série real.
 *
 *  `tempoRespostaMinutos` é o único nullable, e é de propósito: contagem e dinheiro em período
 *  vazio valem zero (é um fato), mas MÉDIA em período vazio não vale zero — zero minuto diria
 *  "respondeu na hora" e a métrica mostraria seu melhor número no dia em que ninguém trabalhou.
 *  O período em si nunca falta; é isso que impede o gráfico de mentir sobre a tendência. */
export interface PontoSerieReal {
  data: string;
  leads: number;
  vendas: number;
  faturamento: number;
  tempoRespostaMinutos: number | null;
}

export interface SerieTemporalDto {
  de: string;
  ate: string;
  agrupamento: AgrupamentoSerie;
  pontos: PontoSerieReal[];
}

// ---------------------------------------------------------------- atividades (REAL)
export type TipoAtividadeReal = 'mensagem' | 'venda' | 'lembrete' | 'contato';

export interface Atividade {
  tipo: TipoAtividadeReal;
  /** `tipo:id` — desempate estável do cursor, porque o feed une quatro tabelas. */
  chave: string;
  quando: string;
  contatoId: number;
  contatoNome: string;
  titulo: string;
  detalhe: string | null;
  valor: number | null;
  responsavelId: number | null;
  responsavelNome: string | null;
}

export interface PaginaAtividades {
  itens: Atividade[];
  temMais: boolean;
}

/** De onde vêm os leads. SEM cor: a paleta é decisão de apresentação e mora no cliente —
 *  diferente da etapa do funil, cuja cor o dono escolhe no cadastro. */
export interface CampanhaDto {
  nome: string;
  vendas: number;
  valor: number;
}

/** Uma campanha nomeada dentro de uma origem (sub-linha da legenda). */
export interface CampanhaDaOrigemDto {
  nome: string;
  leads: number;
}

/** Uma fatia da rosca, JÁ AGRUPADA pelo servidor (AUD-XX): uma por origem, da maior para a menor,
 *  e — passando de seis — as cinco maiores mais uma `agrupada` com o resto. `percentual` de 0 a
 *  100, e as fatias somam 100. A tela só desenha e rotula. */
export interface FatiaOrigemDto {
  /** A origem em minúsculas; `'outros'` na fatia agrupada. */
  origem: OrigemLead | 'outros';
  agrupada: boolean;
  leads: number;
  percentual: number;
  campanhas: CampanhaDaOrigemDto[];
}

export interface DashboardDto {
  /** NEG-3 · as três campanhas que mais faturaram no mês. */
  campanhas: CampanhaDto[];
  leadsHoje: number;
  aguardandoResposta: number;
  followUpsPendentes: number;
  vendasDoMes: number;
  faturamentoDoMes: number;
  /** ganhos ÷ (ganhos + perdidos) do mês, de 0 a 100 — pronto. Null sem nada decidido. */
  taxaConversaoPercentual: number | null;
  funil: FunilNoPainelDto[];
  /** A linha "Todos" do cartão de funis, somada no servidor (AUD-XX). */
  totalEmNegociacao: number;
  totalValorEmAberto: number;
  /** Quantos leads a rosca representa. */
  leadsTotal: number;
  origens: FatiaOrigemDto[];
  /** POS-1 · a empresa já recebeu alguma mensagem de cliente, em QUALQUER momento da vida dela.
   *  Não é "tem mensagem hoje": vem de `primeira_mensagem_em`, com a tabela de mensagens como
   *  desempate quando a coluna é nula (empresa que já operava antes dela existir). */
  recebeuMensagem: boolean;
  /** POS-1 · existe alguma linha de contato, inclusive anonimizada. */
  temContato: boolean;
}

// ---------------------------------------------------------------- lembretes
export type OrigemLembrete = 'automatico' | 'manual';
export type StatusLembrete = 'pendente' | 'concluido' | 'cancelado';

export interface LembreteDto {
  id: number;
  contatoId: number;
  contatoNome: string;
  conversaId: number | null;
  origem: OrigemLembrete;
  status: StatusLembrete;
  dataAlvo: string;
  horaAlvo: string | null;
  titulo: string;
  observacao: string | null;
  enviaMensagem: boolean;
  responsavelId: number | null;
  responsavelNome: string | null;
  concluidoEm: string | null;
}

// ---------------------------------------------------------------- onboarding
/** Um passo dos primeiros passos. `concluido` é DERIVADO do estado real a cada leitura, nunca
 *  lido de uma flag — empresa cujo WhatsApp caiu volta a ver o passo 1 aceso. */
export interface PassoOnboarding {
  /** ⚠️ UNIÃO FECHADA. `anuncios` (INT-4) só aparece para quem TEM anúncio chegando — o passo é
   *  sobre um fato, não sobre uma oportunidade, e um quarto passo fixo deixaria o checklist
   *  permanentemente incompleto para a padaria que não anuncia. */
  chave: 'conexao' | 'equipe' | 'primeira_mensagem' | 'anuncios';
  titulo: string;
  descricao: string;
  concluido: boolean;
  /** O dono pulou. Só o da equipe e o de anúncios aceitam. */
  dispensado: boolean;
  /** Para onde o passo leva. NULL no passo 3 — ele é espera, não ação. */
  rota: string | null;
  rotuloAcao: string | null;
}

export interface Onboarding {
  passos: PassoOnboarding[];
  concluidos: number;
  total: number;
  completo: boolean;
  dispensado: boolean;
  /** Falta passo E o dono não fechou o painel. */
  mostrar: boolean;
  /** Métrica INTERNA. A tela não exibe e nada promete prazo de implantação. */
  minutosAteAPrimeiraMensagem: number | null;
}

// ---------------------------------------------------------------- configuração
export interface FusoDisponivel {
  id: string;
  rotulo: string;
  offsetAtual: string;
}

export interface ConfiguracaoEmpresa {
  nome: string;
  documento: string | null;
  fusoHorario: string;
  /** Sigla da UF. Só serve para semear os feriados estaduais; nula = só nacionais. */
  uf: string | null;
  janelaHoraInicio: number;
  janelaHoraFim: number;
  /** Bitmask: bit 0 = domingo … bit 6 = sábado. 126 = seg a sáb. */
  janelaDiasSemana: number;
  /** Minutos ÚTEIS. Zero DESLIGA a faixa — é comportamento legítimo. */
  semaforoAmareloMinutos: number;
  semaforoVermelhoMinutos: number;
  /** Dias de conversa parada até o follow-up. Mínimo 1. */
  diasSemRespostaFollowUp: number;
  /** Dias até a venda ser concluída sozinha (NEG-2). ZERO = concluir na hora, e é valor
   *  legítimo: padaria, salão, balcão — a venda nasce e termina no mesmo atendimento. */
  diasParaConcluirVenda: number;
  // ===================== A PESQUISA PÓS-VENDA (NPS-1) =====================
  /** Nasce DESLIGADA: ligada por padrão, toda empresa existente começaria a mandar mensagem
   *  automática para os clientes dela no dia do deploy. */
  npsAtivo: boolean;
  /** Dias entre a conclusão da venda e a pergunta. Três, e não zero: perguntar no mesmo dia mede o
   *  ATENDIMENTO, não o produto — o cliente ainda não usou o que comprou. */
  npsDiasAposConclusao: number;
  /** Dias esperando a nota antes de desistir. SEM REENVIO depois disso. Mínimo 1: com zero, a
   *  pesquisa expiraria no instante do envio. */
  npsDiasExpiracao: number;
  /** A pergunta. `{{saudacao}}`, `{{nome}}` e `{{empresa}}` são trocados no envio.
   *
   *  ⚠️ O PADRÃO USA `{{saudacao}}` E NÃO `"Oi, {{nome}}!"`: quando o WhatsApp não manda o nome do
   *  perfil, o nome do contato é o telefone formatado, e `{{nome}}` vira vazio — saindo "Oi, !" no
   *  WhatsApp do cliente. `{{saudacao}}` decide a pontuação junto com o nome. */
  npsTexto: string;
  /** Agradecimentos OPCIONAIS. Nulo = não envia, e é o padrão: uma segunda automática depois da
   *  primeira dobra o risco do número. ⚠️ A ação humana do detrator acontece de qualquer jeito. */
  npsMensagemPromotor: string | null;
  npsMensagemDetrator: string | null;

  /** POS-1 · o prazo acima só vale quando isto é verdadeiro, e o número é guardado mesmo
   *  desligado — é o que faz religar devolver o prazo antigo. */
  conclusaoAutomatica: boolean;
}

export interface FeriadoDto {
  id: number;
  data: string;
  nome: string;
  abrangencia: 'nacional' | 'estadual' | 'manual';
  ehManual: boolean;
  /** Só para feriado nacional: a empresa marcou que trabalha nesse dia. */
  ignorado: boolean;
}

export interface MinhaConta {
  id: number;
  nome: string;
  email: string;
  papel: PapelUsuario;
  empresaNome: string;
}

// ---------------------------------------------------------------- caixa
export interface ConversaResumo {
  id: number;
  contatoId: number;
  contatoNome: string;
  telefone: string;
  ultimaMensagemPrevia: string | null;
  ultimaMensagemDirecao: DirecaoMensagem | null;
  ultimaMensagemEm: string;
  /** TIMESTAMP, não cor. Ver nucleo/semaforo.ts. */
  aguardandoDesde: string | null;
  naoLidas: number;
  status: StatusConversa;
  responsavelId: number | null;
  responsavelNome: string | null;
  /** ⚠️ NULOS desde o E6: quem acabou de chegar pelo WhatsApp ou por formulário não abre
   *  negociação, e contato sem negociação não está em funil nenhum. Eram `number`/`string` com
   *  fallback 0 e "" no servidor — o zero chegava aqui como se fosse um id de etapa de verdade. */
  etapaId: number | null;
  etapaNome: string | null;
  /** ⚠️ Nomeado pela pergunta que responde: a tela mostra o botão? No servidor é DERIVADO de
   *  `funisDisponiveis` ("a lista não está vazia"), então os dois não têm como divergir. */
  podeAbrirNegociacao: boolean;
  /** Os funis onde "Abrir negociação" pode dar certo, PRONTOS para o seletor. Era
   *  `funisOcupados`, e a tela subtraía da lista do menu — uma regra morando no painel. */
  funisDisponiveis: FunilLivre[];
  /** ⚠️ O par. Era `!contatoGanhou` no cliente, e escondia o botão justamente de quem tem venda
   *  pronta para fechar: o cliente recorrente com um negócio aberto. */
  podeRegistrarVenda: boolean;
  /** NEG-3 · já comprou alguma vez. Não decide mais se o botão aparece; decide o que a faixa diz. */
  contatoGanhou: boolean;
  /** NEG-3 · a campanha detectada NESTE ciclo, ou null. Diferente de `origem`, que é a do
   *  cadastro e não se reescreve. */
  canalDoCiclo: string | null;
  /** Quantas vendas deste contato ainda estão em aberto. Zero + `contatoGanhou` = pedido
   *  entregue, e a etiqueta da etapa passa a mentir se disser "Venda". */
  vendasEmAberto: number;
  /** As etiquetas coladas neste contato.
   *
   *  ⚠️ A linha da lista CORTA no que couber (`.chips-linha`): ela não pode crescer porque alguém
   *  marcou oito. A tela de contato é onde se vê a lista inteira. */
  etiquetas: EtiquetaDto[];
}

export interface MensagemDto {
  id: number;
  direcao: DirecaoMensagem;
  texto: string | null;
  /** 0=erro, 1=enviado, 2=servidor, 3=entregue, 4=lido. */
  ack: number | null;
  enviadaEm: string | null;
  recebidaEm: string | null;
  expiradaEm: string | null;
  erro: string | null;
  tipoMidia: TipoMidia;
  midiaNome: string | null;
  midiaMime: string | null;
  /** Tamanho do anexo, para a tela mostrar "proposta.pdf · 340 KB". */
  midiaBytes: number | null;
  /** Duração do áudio em segundos; `null` nas outras mídias. Aparece no player ANTES de tocar:
   *  ouvir 90 segundos é uma decisão, e o vendedor precisa tomá-la olhando. */
  midiaDuracaoSegundos: number | null;
  enviadoPor: number | null;
  enviadoPorNome: string | null;
  /** ⚠️ SUBSTITUI `deLembrete` (NPS-1): a pergunta da tela e "pessoa ou robo", nao "veio de um
   *  lembrete". O lembrete e so uma das automacoes. */
  automatica: boolean;
  /** `follow_up` | `lembrete` | `nps`; null quando e humana. */
  automacao: string | null;
  /** Instante em que esta mensagem ATRASADA foi gravada; null no caso normal (REC-1).
   *  Ela aparece na posição cronológica dela — o carimbo só explica por que surgiu agora
   *  num ponto da thread que já tinha passado. */
  recuperadaEm: string | null;
}

/** Uma venda do histórico (NEG-1). `canceladaEm` vem preenchido em vez de a linha sumir: a
 *  lista a mostra riscada, porque "o valor mudou de 5.000 para 3.000" é informação, e uma linha
 *  que desaparece não explica nada a quem confere o mês depois. */
export interface VendaDto {
  id: number;
  valor: number;
  fechadaEm: string;
  responsavelId: number | null;
  responsavelNome: string | null;
  observacao: string | null;
  canceladaEm: string | null;
  /** `fechada`, `concluida` ou `cancelada` (NEG-2). */
  // ⚠️ ERA `'fechada' | ...` E JA ESTAVA ERRADO ANTES DO E4e/5: a API passou a mandar
  // `ganha` no E4e/2, e este tipo continuou prometendo `fechada`. Nada quebrou porque nenhum
  // `if` do painel compara com ele — so com `cancelada` e `concluida`, que nao mudaram.
  status: 'ganha' | 'concluida' | 'cancelada';
  concluidaEm: string | null;
}

/** Um evento da trilha de auditoria (AUD-1).
 *
 *  `alteracoes` chega como JSON CRU, de propósito: a tradução para português é texto de
 *  interface, muda com a redação do produto, e fazê-la no servidor obrigaria a um deploy de
 *  backend para corrigir uma frase. */
export interface EventoTrilha {
  id: number;
  entidade: string;
  entidadeId: number;
  acao: string;
  alteracoes: string;
  usuarioId: number | null;
  usuarioNome: string | null;
  ator: string;
  quando: string;
}

/** O aviso de mensagens recuperadas. `null` quando não houve queda nas últimas 24h. */
export interface AvisoRecuperacao {
  mensagens: number;
  conversas: number;
  /** O intervalo em que o CLIENTE escreveu — não o instante em que gravamos. */
  de: string;
  ate: string;
}

export interface RespostaEnviada {
  mensagemId: number;
  /** false = registrada mas não chegou (WhatsApp fora). A mensagem aparece na thread
   *  marcada como "não chegou" — não é erro de requisição. */
  enviada: boolean;
  erro: string | null;
}

// ---------------------------------------------------------------- conexão
export interface Conexao {
  id: number;
  nome: string;
  instanceName: string;
  numero: string | null;
  numeroAnterior: string | null;
  perfilNome: string | null;
  perfilFotoUrl: string | null;
  status: StatusConexao;
  conectadoEm: string | null;
  desconectadoEm: string | null;
  /** Quantas conversas apontam para este número. É a contagem CRUA, a mesma que a FK enxerga. */
  conversas: number;
  /** Vêm do SERVIDOR, não são deduzidos aqui: só o banco sabe se há conversa apontando para a
   *  conexão. Sem isso a tela ofereceria um botão que às vezes devolve erro — a pior forma de
   *  dizer "não pode". */
  podeRemover: boolean;
  motivoNaoRemove: string | null;
}

/** A lista + o que o PLANO permite. O limite vem junto porque a tela precisa dele para decidir
 *  se mostra "adicionar" — e um limite que a tela adivinha diverge do que o servidor aplica no
 *  dia em que o contrato muda. */
export interface Conexoes {
  itens: Conexao[];
  limite: number;
  podeAdicionar: boolean;
}

export interface StatusConexaoDto {
  instanceName: string;
  /** Estado cru da Evolution: open | connecting | close | nao_criada | offline. */
  estado: string;
  conectado: boolean;
}

export interface QrCode {
  base64: string | null;
  codigo: string | null;
  pairingCode: string | null;
  estado: string;
  conectado: boolean;
}

export interface SaudeConexao {
  enviadasHoje: number;
  pendentes: number;
  expiradas: number;
  falhasHoje: number;
}

// ---------------------------------------------------------------- canais (QR / link)

/** Um canal de captação por QR Code ou link rastreável.
 *
 *  `link`, `texto`, `nomeArquivo` e `podeRemover` vêm do SERVIDOR e não são montados aqui: o link
 *  depende do número da conexão, o texto é a mesma string que o webhook vai procurar, e só o
 *  banco sabe se já chegou lead por este canal. */
/** O teto da frase do link do canal. O MESMO numero de `CodigoCanal.LimiteMensagem` no
 *  servidor — aqui e conveniencia (o `maxlength` e o contador); quem recusa e o servico.
 *
 *  Existe pela mesma razao do codigo curto: texto pre-preenchido longo parece spam, e a pessoa
 *  apaga tudo antes de enviar — levando o codigo de rastreio junto. */
export const LIMITE_MENSAGEM_CANAL = 120;

export interface CanalDto {
  id: number;
  nome: string;
  codigo: string;
  conexaoId: number;
  conexaoNome: string;
  /** Nulo quando a conexão perdeu o pareamento — e aí `link` também é nulo e o QR não sai. */
  numero: string | null;
  origem: OrigemLead;
  ativo: boolean;
  leadsRecebidos: number;
  /** A frase que o dono escreveu, SEM o codigo — e ela que volta para o campo de edicao.
   *  `texto` e o resultado final, com o codigo. */
  mensagem: string | null;
  link: string | null;
  texto: string;
  /** Sem extensão. O download é por blob, e blob não carrega `Content-Disposition`. */
  nomeArquivo: string;
  podeRemover: boolean;
  motivoNaoRemove: string | null;
  criadoEm: string;
}

export interface ConexaoParaCanal {
  id: number;
  nome: string;
  numero: string;
}

export interface Canais {
  itens: CanalDto[];
  /** Só as conexões com número pareado: sem número, o link sai quebrado. */
  conexoes: ConexaoParaCanal[];
  podeCriar: boolean;
  /** Soma dos leads atribuídos. É PISO, não total — quem apagou o código antes de enviar
   *  entrou como `whatsapp` e não aparece aqui. */
  leadsAtribuidos: number;
}

// ---------------------------------------------------------------- webhook de saída

export type EventoWebhook =
  | 'lead.criado' | 'lead.movido' | 'venda.fechada' | 'venda.perdida'
  | 'mensagem.recebida' | 'webhook.teste';

export type StatusEntrega = 'pendente' | 'entregue' | 'falhou';

/** A configuração do webhook. **Nunca traz o segredo** — ele sai uma vez, na criação e ao ser
 *  regerado. Um segredo que a tela busca a cada carregamento vive no histórico do navegador e no
 *  cache do proxy. */
export interface WebhookDto {
  id: number;
  url: string;
  ativo: boolean;
  somenteIds: boolean;
  emLeadCriado: boolean;
  emLeadMovido: boolean;
  emVendaFechada: boolean;
  emVendaPerdida: boolean;
  emMensagemRecebida: boolean;
  criadoEm: string;
}

export interface EntregaWebhookDto {
  id: number;
  evento: EventoWebhook;
  status: StatusEntrega;
  tentativas: number;
  codigoResposta: number | null;
  erro: string | null;
  proximaTentativaEm: string | null;
  entregueEm: string | null;
  criadoEm: string;
  /** O corpo exato que foi assinado. Vai junto porque "o cliente diz que não recebeu" costuma
   *  terminar em "o que exatamente vocês mandaram?". */
  payload: string;
  podeReenviar: boolean;
}

export interface PainelWebhook {
  webhook: WebhookDto | null;
  entregas: EntregaWebhookDto[];
}

export interface SalvarWebhook {
  url: string;
  ativo: boolean;
  somenteIds: boolean;
  emLeadCriado: boolean;
  emLeadMovido: boolean;
  emVendaFechada: boolean;
  emVendaPerdida: boolean;
  emMensagemRecebida: boolean;
}

/** O segredo, entregue uma vez. `novo` distingue "acabei de criar" de "regerei". */
export interface SegredoRevelado {
  id: number;
  segredo: string;
  novo: boolean;
}

export interface ResultadoTeste {
  ok: boolean;
  codigo: number | null;
  erro: string | null;
}

// ==================================================================== conversões de anúncio (INT-4)
/** A credencial de anúncio da empresa.
 *
 *  ⚠️ `tokenFinal` é SUFIXO MASCARADO, não o token. A API nunca devolve o token — nem na criação,
 *  ao contrário do segredo do webhook: aquele nós geramos, este o cliente cola do Gerenciador de
 *  Eventos da Meta, e não há o que revelar. */
export interface CredencialDto {
  id: number;
  plataforma: string;
  identificador: string;
  tokenFinal: string | null;
  codigoTeste: string | null;
  /** O id da página do Facebook. Só o caminho do Clique-para-WhatsApp usa — sem ele o lead do
   *  WhatsApp sai como `chat`, que funciona e casa por telefone. */
  paginaId: string | null;
  ativo: boolean;
  emLead: boolean;
  emCompra: boolean;
  consentimentoEm: string | null;
  consentimentoPor: string | null;
  desativadaEm: string | null;
  desativadaMotivo: string | null;
  criadoEm: string;
  /** Está de fato enviando? Derivado no SERVIDOR — a regra de quando um evento pode sair é uma só,
   *  e mora em `CredencialConversao.PodeEnviar`.
   *
   *  ⚠️ ISTO JÁ FOI CALCULADO AQUI NO CLIENTE, e estava errado: esquecia `emLead`/`emCompra`, então
   *  desmarcar "Purchase" deixava o selo verde enquanto toda venda caía no chão. */
  enviando: boolean;

  /** POR QUE o envio de VENDA está parado, em frases prontas. Vazia = está enviando.
   *
   *  ⚠️ VEM PRONTO DO SERVIDOR, e a tela só imprime. Montar a frase aqui seria reescrever o
   *  `PodeEnviar` numa segunda língua — que é exatamente como o `enviando` acabou errado. */
  motivosParados: string[];
}

export interface SalvarCredencial {
  identificador: string;
  /** Vazio MANTÉM o token anterior: a tela não tem como preenchê-lo de volta. */
  token: string | null;
  codigoTeste: string | null;
  paginaId: string | null;
  ativo: boolean;
  emLead: boolean;
  emCompra: boolean;
  consentimentoDeclarado: boolean;
}

/** Um evento no registro. `payload` vai junto pela mesma razão do registro de webhooks: "não está
 *  chegando na Meta" termina sempre em "o que exatamente vocês mandaram?" — e ele NÃO tem o token
 *  dentro, porque o token só entra na hora do envio. */
export interface ConversaoDto {
  id: number;
  tipo: string;
  status: string;
  contato: string;
  valor: number | null;
  tentativas: number;
  codigoResposta: number | null;
  codigoMeta: number | null;
  fbtraceId: string | null;
  erro: string | null;
  ocorridoEm: string;
  expiraEm: string;
  entregueEm: string | null;
  criadoEm: string;
  payload: string;
  /** ⚠️ `expirado` nunca pode: a janela de 7 dias da Meta é recusa do mundo, e oferecer o botão
   *  seria oferecer um gesto que só pode fracassar. Quem decide é o SERVIDOR. */
  podeReenviar: boolean;
}

/** Só as duas contas do aviso da Captação — sem a credencial e sem as 50 últimas conversões. */
export interface ResumoConversoes {
  enviando: boolean;
  leadsComAnuncio30Dias: number;
}

export interface ResultadoTesteConversao {
  ok: boolean;
  codigo: number | null;
  fbtraceId: string | null;
  erro: string | null;
}

/** Uma venda que fechou e nunca virou evento (INT-5).
 *
 *  Não há linha para ler: quando o portão da credencial está fechado, o publicador devolve `void`
 *  sem gravar nada. A lista é DERIVADA — venda fechada que não tem evento. */
export interface VendaSemConversaoDto {
  negociacaoId: number;
  contato: string;
  valor: number | null;
  ganhaEm: string;
  /** `ganhaEm + 7 dias`. A tela desenha a contagem a partir daqui. */
  expiraEm: string;
  /** ⚠️ DECIDIDO NO SERVIDOR, com o relógio dele. A tela não recalcula a janela: o botão aparece
   *  ou não a partir DESTE campo. Cliente e servidor discordando sobre "passou do prazo" produziria
   *  um botão que só pode fracassar. */
  foraDoPrazo: boolean;
}

/** A lista mais os dois números que fazem alguém agir. `total` e `valorTotal` vêm de consulta
 *  própria: a lista tem teto, e somar o que coube diria menos que a verdade. */
export interface VendasSemEnvio {
  total: number;
  valorTotal: number;
  /** Quantas ainda dão tempo, contadas pelo servidor sobre TODAS — e não sobre as 50 da lista. */
  noPrazo: number;
  /** A janela em dias, para a tela não repetir o número. */
  diasDaJanela: number;
  vendas: VendaSemConversaoDto[];
}

export interface ResultadoEnvioEmLote {
  enfileiradas: number;
  /** O que sobrou além do teto de uma rodada do motor. */
  restantes: number;
}

export interface PainelConversoes {
  credencial: CredencialDto | null;
  /** Quantos leads dos últimos 30 dias chegaram com identificador de anúncio. É o número que
   *  cobra quem não conectou — e, para quem conectou, a conferência de que o rastro está
   *  chegando. */
  leadsComAnuncio30Dias: number;
  conversoes: ConversaoDto[];
  /** INT-5 · as vendas que fecharam sem avisar a Meta. `total` zero é o normal. */
  vendasSemEnvio: VendasSemEnvio;
}

// ---------------------------------------------------------------- equipe
export interface UsuarioEquipe {
  id: number;
  nome: string;
  email: string;
  papel: PapelUsuario;
  status: StatusUsuario;
  ultimoAcessoEm: string | null;

  /** O que esta pessoa pode HOJE — o papel dela MAIS ou MENOS o que o dono ajustou. É o efetivo,
   *  já calculado no servidor; a tela só marca os interruptores com ele. */
  permissoes: Permissao[];
}

export interface TokenGerado {
  usuarioId: number;
  token: string;
}

export interface ConviteInfo {
  nome: string;
  email: string;
  empresaNome: string;
}

// ---------------------------------------------------------------- realtime
export interface MensagemPainel {
  id: number;
  conversaId: number;
  contatoId: number;
  contatoNome: string;
  previa: string | null;
  direcao: DirecaoMensagem;
  em: string;
}

export interface ConversaPainel {
  id: number;
  contatoId: number;
  contatoNome: string;
  telefone: string;
}

export interface ContatoPainel {
  id: number;
  nome: string;
  telefone: string;
  etapaId: number;
}

export interface ConexaoPainel {
  id: number;
  status: StatusConexao;
  numero: string | null;
  numeroAnterior: string | null;
}

export interface StatusMensagemPainel {
  mensagemId: number;
  ack: number;
}

// ---------------------------------------------------------------- captação por formulário

/** Um formulário de captação publicado no site do cliente.
 *
 *  A `chave` é o que abre um endpoint de ESCRITA na internet — ela aparece na tela para ser
 *  copiada para o HTML, e some do ar assim que é regerada. */
export interface FormularioDto {
  id: number;
  nome: string;
  chave: string;
  dominioPermitido: string | null;
  ativo: boolean;
  leadsRecebidos: number;
  criadoEm: string;
}

// ---------------------------------------------------------------- configuração do funil

/** Uma etapa na tela de CONFIGURAÇÃO do funil (a do quadro é `EtapaFunil`).
 *
 *  `contatos` é a contagem CRUA — inclui perdido e anonimizado, que continuam apontando para a
 *  etapa e travando a remoção pela FK. É diferente do número que o kanban mostra, e é de
 *  propósito: aqui o número responde "o que impede de apagar". */
export interface EtapaConfigDto {
  id: number;
  nome: string;
  ordem: number;
  cor: string;
  eGanho: boolean;
  contatos: number;
}

/** Uma etiqueta do vocabulário da empresa.
 *
 *  ⚠️ SEM CONTAGEM DE USO, diferente de `EtapaConfigDto`. Lá o número responde "o que impede de
 *  apagar"; aqui apagar é sempre livre — a etiqueta não segura ninguém, o contato só perde um
 *  rótulo. Um número que é sempre zero só ensina a ignorá-lo.
 *
 *  Sem `ordem` também: etiqueta não tem sequência, a lista vem por nome. */
/** A etiqueta na TELA DE GESTÃO, com quantos contatos a usam.
 *
 *  Separada do `EtiquetaDto` porque a contagem é uma subconsulta por linha — pô-la no chip faria
 *  todo card do quadro e toda linha da caixa pagarem por um número que nenhum dos dois mostra. */
export interface EtiquetaNaLista extends EtiquetaDto {
  /** ⚠️ CONTAGEM CRUA: inclui contato perdido e anonimizado. É o número que responde "de quantos
   *  contatos esta etiqueta sai se eu apagar" — a pergunta que o dono faz antes de apagar. */
  contatos: number;
}

export interface EtiquetaDto {
  id: number;
  nome: string;
  cor: string;
}

// ---------------------------------------------------------------- cadastro de empresa (operador)
/** O corpo de `POST /api/cadastro/empresa` — espelha o record `NovaEmpresa` em
 *  `src/Nexora.Core/Servicos/IServicoCadastroEmpresa.cs:6`.
 *
 *  ⚠️ OS CINCO OBRIGATÓRIOS VÃO SEMPRE, MESMO VAZIOS, e não é zelo: no servidor eles são `string`
 *  não anulável num record posicional, e com nullable ligado o `[ApiController]` os trata como
 *  obrigatórios. OMITIR um devolve `ValidationProblemDetails` — `{ title, status, errors }`, em
 *  inglês, moldado pelo framework — que a tela não sabe ler e que o operador não entenderia.
 *  MANDAR vazio cai nas validações do próprio serviço, que respondem `{ erro }` em português.
 *
 *  `nomeConexao` e `instanceName` existem no record e NÃO entram aqui de propósito: o servidor
 *  usa "Principal" e deriva `emp-{id}`, e nenhum dos dois é decisão de quem cadastra. */
export interface NovaEmpresa {
  nome: string;
  /** CNPJ. O servidor guarda só os dígitos (`ServicoCadastroEmpresa.cs:61`). */
  documento: string | null;
  nomeDono: string;
  emailDono: string;
  senha: string;
}

/** ⚠️ O `empresaId` é o ÚNICO identificador durável que o cadastro devolve, e a tela tem de
 *  mostrá-lo: é dele que o nome da instância da Evolution é derivado (`emp-{id}`), então sem ele
 *  não se acha este cliente nem no painel da Evolution nem no banco. */
export interface EmpresaCriada {
  empresaId: number;
}

// ---------------------------------------------------------------- área do operador (OPE-1)
/** Envelope de página, igual ao `Pagina<T>` do backend (`Dtos/Comum.cs`). */
export interface Pagina<T> {
  total: number;
  numeroPagina: number;
  tamanho: number;
  itens: T[];
}

/** Uma empresa cliente na lista do operador. ⚠️ NÚMEROS, E SÓ — nenhum nome de contato, telefone
 *  ou texto de mensagem passa por aqui. A área responde "como vai este cliente", não "o que ele
 *  está conversando". */
export interface EmpresaNaLista {
  id: number;
  nome: string;
  ativa: boolean;
  demonstracao: boolean;
  planoId: number | null;
  planoNome: string | null;
  limiteConexoes: number;
  limiteUsuarios: number;
  /** Os limites fugiram do molde do plano — alguém ajustou esta empresa à mão. */
  limitesPersonalizados: boolean;
  criadaEm: string;
  /** ⚠️ O tempo entre a empresa assinar e o produto funcionar. Nulo enquanto a primeira mensagem
   *  não chegou — e empresa parada aí é empresa que nunca pareou o WhatsApp. */
  horasAteValor: number | null;
  usuariosAtivos: number;
  usuariosConvidados: number;
  vagasUsadas: number;
  conexoes: number;
  conexoesConectadas: number;
  contatos: number;
  ultimoAcessoEm: string | null;
  ultimaMensagemEm: string | null;
  negociacoesAbertas: number;
  ganhasNaJanela: number;
  valorGanhoNaJanela: number;
}

export interface LimitesDaEmpresa {
  empresaId: number;
  nome: string;
  ativa: boolean;
  planoId: number | null;
  planoNome: string | null;
  limiteConexoes: number;
  conexoesUsadas: number;
  limiteUsuarios: number;
  vagasUsadas: number;
}

/** ⚠️ `preco` NÃO COBRA NADA. Não existe cobrança neste sistema — é o registro do que foi
 *  combinado, lido por uma pessoa. A tela precisa dizer isso, porque um campo de preço ao lado de
 *  um botão "atribuir plano" se lê como se mudasse o que o cliente paga. */
export interface PlanoDto {
  id: number;
  nome: string;
  preco: number;
  limiteConexoes: number;
  limiteUsuarios: number;
  ativo: boolean;
  ordem: number;
  /** Quantas empresas estão neste plano. É o número que responde "posso arquivar sem deixar
   *  ninguém órfão". */
  empresasNoPlano: number;
}

export interface NovoPlano {
  nome: string;
  preco: number;
  limiteConexoes: number;
  limiteUsuarios: number;
  ordem: number;
}

export interface EditarPlano extends NovoPlano {
  ativo: boolean;
}

/** `confirmarExcedente` existe porque baixar um teto abaixo do uso é LEGAL e quase sempre não é o
 *  que a pessoa quis. Sem ele o servidor recusa e explica que ninguém perde acesso. */
export interface AjusteDeLimites {
  limiteConexoes: number;
  limiteUsuarios: number;
  confirmarExcedente?: boolean;
}
