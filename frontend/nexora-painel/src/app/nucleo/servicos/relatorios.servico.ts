import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API } from '../api-base';
import { Pagina } from '../modelos';

/** A barra de filtros da tela, do jeito que vai para a query string.
 *
 *  ⚠️ NÃO EXISTE faixa de valor global aqui, e a ausência é deliberada: `contatos.valor` é
 *  estimativa em aberto e `vendas.valor` é o que fechou. Os campos de valor pertencem aos
 *  relatórios que declaram sobre qual grandeza agem, e o rótulo da tela diz qual. */
export interface FiltroRelatorio {
  de: string;
  ate: string;
  agrupamento: 'dia' | 'semana' | 'mes';
  responsavelId?: number | null;
  origem?: string | null;
  etapaId?: number | null;
  status?: 'ganha' | 'concluida' | 'cancelada' | null;
  motivoPerda?: string | null;
  valorMin?: number | null;
  valorMax?: number | null;
}

export interface PontoVendas {
  periodo: string;
  /** Tudo que NÃO foi cancelado. `concluidas` é um subconjunto disto. */
  vendas: number;
  faturamento: number;
  concluidas: number;
  valorConcluido: number;
  /** FORA do total, e mostrado à parte: a linha não some do relatório. */
  canceladas: number;
  valorCancelado: number;
}

export interface TotaisVendas extends Omit<PontoVendas, 'periodo'> {
  ticketMedio: number;
}

/** ===================== UM NÚMERO CONTRA O MESMO NÚMERO DE ANTES (CMP-1) =====================
 *  ⚠️ `tendencia` É O MOVIMENTO E `avaliacao` É O JUÍZO, e são dois campos de propósito: a SETA
 *  segue o movimento, a COR segue a avaliação. "Cancelado" caindo é seta para baixo e verde — se a
 *  cor seguisse a seta, a tela pintaria de vermelho a melhor notícia do mês.
 *
 *  ⚠️ `variacaoPercentual` NULO = não havia nada antes. Dividir por zero não é −100% nem infinito,
 *  e a frase que explica isso é POR INDICADOR: "sem cancelamento em ago" não é "novo".
 *  ============================================================================================ */
export interface IndicadorComparativo {
  atual: number;
  anterior: number;
  variacaoAbsoluta: number;
  variacaoPercentual: number | null;
  tendencia: 'subiu' | 'caiu' | 'estavel';
  avaliacao: 'melhor' | 'pior' | 'neutro';
  anteriorDe: string;
  anteriorAte: string;
}

export interface ComparativoVendas {
  vendas: IndicadorComparativo;
  faturamento: IndicadorComparativo;
  concluidas: IndicadorComparativo;
  valorConcluido: IndicadorComparativo;
  canceladas: IndicadorComparativo;
  valorCancelado: IndicadorComparativo;
  ticketMedio: IndicadorComparativo;

  /** O recorte EFETIVO do período atual: com o mês em andamento, para em hoje. */
  de: string;
  ate: string;
  emAndamento: boolean;
}

export interface RelatorioVendas {
  pontos: PontoVendas[];
  totais: TotaisVendas;

  /** Nulo até o servidor com o CMP-1 subir — a tela não pode depender dele existir. */
  comparativo?: ComparativoVendas | null;
}

/** ===================== A PESQUISA PÓS-VENDA (NPS-1) =====================
 *  ⚠️ O EIXO É O DIA EM QUE A PESQUISA SAIU (a coorte), não o da resposta: de tudo que saiu no
 *  período, quanto voltou e com que nota. É o que dá denominador à taxa de resposta.
 *
 *  `nps` e `taxaDeResposta` NULOS = não há base. Zero é um NPS real (tantos promotores quanto
 *  detratores) e a tela não pode confundir os dois.
 *  ======================================================================= */
export interface TotaisNps {
  enviadas: number;
  respondidas: number;
  expiradas: number;
  canceladas: number;
  /** Ainda no prazo, ou com nota em dúvida esperando o vendedor. É o número que explica uma taxa
   *  baixa no período que termina hoje. */
  aindaAbertas: number;
  promotores: number;
  neutros: number;
  detratores: number;
  nps: number | null;
  taxaDeResposta: number | null;
}

export interface FatiaDaNota {
  nota: number;
  quantas: number;
}

export interface ComparativoNps {
  nps: IndicadorComparativo;
  respondidas: IndicadorComparativo;
  taxaDeResposta: IndicadorComparativo;
  promotores: IndicadorComparativo;
  de: string;
  ate: string;
  emAndamento: boolean;
}

export interface RelatorioNps {
  totais: TotaisNps;
  /** As onze notas, de 0 a 10, sempre — inclusive as de contagem zero. */
  distribuicao: FatiaDaNota[];
  comparativo?: ComparativoNps | null;
}

/** Uma resposta da pesquisa, uma a uma (NPS-1 3.3).
 *
 *  `ultimaCompraEm` é a compra mais recente do cliente, qualquer uma; `comprouDeNovoEm` é a
 *  PRIMEIRA depois da avaliada, nula quando ele não voltou. Compra cancelada não conta — é o
 *  servidor que decide, e a tela só mostra. */
export interface LinhaRespostaNps {
  pesquisaId: number;
  contatoId: number;
  cliente: string;
  nota: number;
  dataResposta: string;
  comentario: string | null;
  responsavelId: number | null;
  responsavel: string | null;
  ultimaCompraEm: string | null;
  comprouDeNovoEm: string | null;
}

/** Os dois atalhos prontos (3.4). ⚠️ O NOME É O DO ENUM DO SERVIDOR, e ele decide o que cada um
 *  significa — inclusive que o atalho IGNORA o período da barra e vence a faixa escolhida. */
export type AtalhoRespostas = 'Nenhum' | 'PromotoresQueNaoVoltaram' | 'DetratoresSemRetorno';

export interface FiltroRespostas {
  faixa: 'promotor' | 'neutro' | 'detrator' | null;
  comprouDeNovo: boolean | null;
  atalho: AtalhoRespostas;
  diasSemCompra: number;
}

export interface LinhaVendedor {
  usuarioId: number | null;
  nome: string;
  leadsAtendidos: number;
  vendas: number;
  valor: number;
  ticketMedio: number;
  conversao: number;
}

/** NEG-3 · uma campanha e o que ela faturou no período. `canal` nulo = venda sem canal
 *  identificado, que é a maioria e aparece na tabela como uma linha própria. */
export interface LinhaCanalVenda {
  canal: string | null;
  vendas: number;
  valor: number;
}

export interface LinhaOrigem {
  origem: string;
  leads: number;
  vendas: number;
  valor: number;
  conversao: number;
}

export interface EntradaEtapa {
  etapaId: number;
  nome: string;
  ordem: number;
  cor: string;
  entradas: number;
  /** ⚠️ `ordem` é única POR FUNIL, não por empresa — duas etapas diferentes têm ordem 1. É o funil
   *  que diz de quem a etapa é, e a lista vem da API já ordenada por funil e depois por etapa. */
  pipelineId: number;
  pipelineNome: string;
}

export interface EtapaAgora {
  etapaId: number;
  nome: string;
  ordem: number;
  cor: string;
  contatos: number;
  valor: number;
  pipelineId: number;
  pipelineNome: string;
}

export interface RelatorioFunil {
  entradas: EntradaEtapa[];
  agora: EtapaAgora[];
  /** Desde quando existe movimentação registrada. Ver o comentário da tela: sem esta data, um
   *  cliente de um ano vê zero entradas e conclui que o relatório está quebrado. */
  trilhaComecaEm: string | null;
}

export interface LinhaTempoResposta {
  usuarioId: number | null;
  nome: string;
  respostas: number;
  mediaMinutos: number;
  medianaMinutos: number;
}

export interface LinhaMotivoPerda {
  motivo: string;
  contatos: number;
  valorPerdido: number;
}

export interface LinhaClienteRecorrente {
  contatoId: number;
  nome: string;
  telefone: string;
  compras: number;
  total: number;
  ultimaEm: string;
}

export interface OpcaoFiltro { id: number; nome: string; }

/** Uma etapa E O FUNIL dela.
 *
 *  ⚠️ O mesmo nome de etapa existe nos dois funis de proposito. Sem o funil, "Primeiro
 *  Atendimento" aparece duas vezes no seletor e escolher a errada recorta o relatorio INTEIRO pelo
 *  outro processo — sem erro e sem aviso. */
export interface OpcaoEtapa {
  id: number;
  nome: string;
  pipelineId: number;
  pipelineNome: string;
}

/** O que a barra de filtros precisa para se desenhar. `responsaveis` vem com UMA entrada quando
 *  quem pede é vendedor — é assim que o seletor nasce travado, sem a tela precisar decidir. */
export interface OpcoesRelatorio {
  responsaveis: OpcaoFiltro[];
  etapas: OpcaoEtapa[];
  /** Os motivos REALMENTE usados. O campo é texto livre; uma lista fixa daria filtro que nunca
   *  casa com o que foi digitado. */
  motivosPerda: string[];
}

/** Os sete relatórios.
 *
 *  O recorte por papel acontece no SERVIDOR: vendedor recebe só os próprios números, e mandar
 *  `responsavelId` de outra pessoa não muda nada. A tela trava o seletor por cortesia, não por
 *  segurança — quem decide é a API. */
@Injectable({ providedIn: 'root' })
export class RelatoriosServico {
  private http = inject(HttpClient);

  /** As listas da barra, numa chamada só e já recortadas por papel no servidor.
   *
   *  Não sai de `/equipe` nem de `/etapas`: as duas são `[Authorize(Roles="dono")]`, e o gestor —
   *  que vê o relatório inteiro — levaria 403 montando o próprio filtro. */
  opcoes(): Observable<OpcoesRelatorio> {
    return this.http.get<OpcoesRelatorio>(`${API}/relatorios/opcoes`);
  }

  vendas(f: FiltroRelatorio): Observable<RelatorioVendas> {
    return this.http.get<RelatorioVendas>(`${API}/relatorios/vendas`, { params: params(f) });
  }

  vendedores(f: FiltroRelatorio): Observable<LinhaVendedor[]> {
    return this.http.get<LinhaVendedor[]>(`${API}/relatorios/vendedores`, { params: params(f) });
  }

  origens(f: FiltroRelatorio): Observable<LinhaOrigem[]> {
    return this.http.get<LinhaOrigem[]>(`${API}/relatorios/origens`, { params: params(f) });
  }

  /** NEG-3 · faturamento por campanha. Endpoint separado do de origens porque são chaves e
   *  recortes diferentes — ver `LinhaCanalVenda` no servidor. */
  canais(f: FiltroRelatorio): Observable<LinhaCanalVenda[]> {
    return this.http.get<LinhaCanalVenda[]>(`${API}/relatorios/canais`, { params: params(f) });
  }

  funil(f: FiltroRelatorio): Observable<RelatorioFunil> {
    return this.http.get<RelatorioFunil>(`${API}/relatorios/funil`, { params: params(f) });
  }

  tempoResposta(f: FiltroRelatorio): Observable<LinhaTempoResposta[]> {
    return this.http.get<LinhaTempoResposta[]>(
      `${API}/relatorios/tempo-resposta`, { params: params(f) });
  }

  nps(f: FiltroRelatorio): Observable<RelatorioNps> {
    return this.http.get<RelatorioNps>(`${API}/relatorios/nps`, { params: params(f) });
  }

  /** A barra inteira vai junto, como em toda rota daqui: o servidor usa período e responsável e
   *  ignora o resto. Os filtros da lista só seguem quando escolhidos — `comprouDeNovo` nulo é
   *  "tanto faz", e mandar `false` no lugar dele esconderia metade da lista. */
  respostasNps(
    f: FiltroRelatorio, r: FiltroRespostas, pagina: number, tamanho = 20
  ): Observable<Pagina<LinhaRespostaNps>> {
    let p = params(f)
      .set('atalho', r.atalho)
      .set('diasSemCompra', r.diasSemCompra)
      .set('pagina', pagina)
      .set('tamanho', tamanho);

    if (r.faixa !== null) p = p.set('faixa', r.faixa);
    if (r.comprouDeNovo !== null) p = p.set('comprouDeNovo', r.comprouDeNovo);

    return this.http.get<Pagina<LinhaRespostaNps>>(`${API}/relatorios/nps/respostas`, { params: p });
  }

  perdas(f: FiltroRelatorio): Observable<LinhaMotivoPerda[]> {
    return this.http.get<LinhaMotivoPerda[]>(`${API}/relatorios/perdas`, { params: params(f) });
  }

  recorrentes(f: FiltroRelatorio, pagina: number, tamanho = 20): Observable<Pagina<LinhaClienteRecorrente>> {
    return this.http.get<Pagina<LinhaClienteRecorrente>>(`${API}/relatorios/recorrentes`, {
      params: params(f).set('pagina', pagina).set('tamanho', tamanho)
    });
  }

  /** O CSV vem PRONTO do servidor.
   *
   *  Montá-lo aqui exigiria buscar todas as páginas do relatório de recorrentes, concatenar em
   *  memória e travar a aba — e o arquivo sairia diferente do que a API produz. Um lugar só que
   *  sabe formatar número em pt-BR e escapar campo.
   *
   *  `responseType: 'blob'` e não texto: o BOM UTF-8 do começo é BYTE, e o parser de texto do
   *  HttpClient o transformaria em caractere invisível no meio do primeiro cabeçalho. */
  csv(nome: string, f: FiltroRelatorio): Observable<Blob> {
    return this.http.get(`${API}/relatorios/${nome}/csv`, {
      params: params(f),
      responseType: 'blob'
    });
  }
}

/** Só o que foi preenchido entra na query string. Mandar `origem=` vazio faria o servidor tentar
 *  interpretar string vazia como enum e devolver 400. */
function params(f: FiltroRelatorio): HttpParams {
  let p = new HttpParams()
    .set('de', f.de)
    .set('ate', f.ate)
    .set('agrupamento', f.agrupamento);

  const opcionais: [string, unknown][] = [
    ['responsavelId', f.responsavelId],
    ['origem', f.origem],
    ['etapaId', f.etapaId],
    ['status', f.status],
    ['motivoPerda', f.motivoPerda],
    ['valorMin', f.valorMin],
    ['valorMax', f.valorMax]
  ];

  for (const [chave, valor] of opcionais) {
    if (valor !== null && valor !== undefined && valor !== '') p = p.set(chave, String(valor));
  }

  return p;
}
