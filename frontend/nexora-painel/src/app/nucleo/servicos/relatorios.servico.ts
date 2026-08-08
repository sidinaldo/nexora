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
  status?: 'fechada' | 'concluida' | 'cancelada' | null;
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

export interface RelatorioVendas {
  pontos: PontoVendas[];
  totais: TotaisVendas;
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
}

export interface EtapaAgora {
  etapaId: number;
  nome: string;
  ordem: number;
  cor: string;
  contatos: number;
  valor: number;
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

/** O que a barra de filtros precisa para se desenhar. `responsaveis` vem com UMA entrada quando
 *  quem pede é vendedor — é assim que o seletor nasce travado, sem a tela precisar decidir. */
export interface OpcoesRelatorio {
  responsaveis: OpcaoFiltro[];
  etapas: OpcaoFiltro[];
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
