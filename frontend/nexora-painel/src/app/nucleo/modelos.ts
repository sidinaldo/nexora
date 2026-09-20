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
  | 'configurar_empresa'
  | 'gerenciar_equipe'
  | 'importar_contatos'
  | 'cancelar_venda'
  | 'ver_historico'
  | 'anonimizar_contato'
  | 'cadastrar_feriado'
  | 'ver_numeros_da_equipe';

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
  /** Do conjunto INTEIRO da coluna, não da página carregada. */
  total: number;
  valorTotal: number;
  /** Quantas vendas já foram CONCLUÍDAS nesta etapa (NEG-2). Zero fora da etapa de ganho.
   *  Sem esse segundo número, a coluna esvaziando pareceria perda de dado. */
  concluidas: number;
  contatos: CardFunil[];
  temMais: boolean;
}

export interface QuadroFunil {
  colunas: ColunaFunil[];
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

// ---------------------------------------------------------------- dashboard
export interface EtapaFunilDto {
  etapaId: number;
  nome: string;
  ordem: number;
  cor: string;
  contatos: number;
  valor: number;
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

export interface OrigemDto {
  origem: OrigemLead;
  leads: number;
  /** NEG-3 · o nome da campanha que capturou o lead, ou null. Vem do servidor porque só ele
   *  conhece `origem_detalhe`; a tela usa este nome no lugar do rótulo genérico da origem. */
  campanha: string | null;
}

export interface DashboardDto {
  /** NEG-3 · as três campanhas que mais faturaram no mês. */
  campanhas: CampanhaDto[];
  leadsHoje: number;
  aguardandoResposta: number;
  followUpsPendentes: number;
  vendasDoMes: number;
  faturamentoDoMes: number;
  /** Fração de 0 a 1 (ganhos ÷ fechados do mês). */
  taxaConversao: number;
  funil: EtapaFunilDto[];
  origens: OrigemDto[];
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
  chave: 'conexao' | 'equipe' | 'primeira_mensagem';
  titulo: string;
  descricao: string;
  concluido: boolean;
  /** O dono pulou. Só o passo da equipe aceita. */
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
  deLembrete: boolean;
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

// ---------------------------------------------------------------- equipe
export interface UsuarioEquipe {
  id: number;
  nome: string;
  email: string;
  papel: PapelUsuario;
  status: StatusUsuario;
  ultimoAcessoEm: string | null;
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
