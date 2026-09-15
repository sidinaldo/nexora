import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { OpcaoCanal } from '../fechamento/modal-fechamento';

export interface CanaisDoFechamento {
  detectadoId: number | null;
  canais: OpcaoCanal[];
}
import { API } from '../api-base';
import { PipelinesServico, recontarMenu } from './pipelines.servico';
import { ContatoDetalhe, ContatoResumo, FiltroContato, OrigemLead, Pagina } from '../modelos';

export interface CorpoContato {
  nome: string;
  telefone: string;
  email?: string | null;
  origem?: OrigemLead | null;
  origemDetalhe?: string | null;
  etapaId?: number | null;
  responsavelId?: number | null;
  valor?: number | null;
  observacoes?: string | null;
}

/** Os contatos. A ETAPA não entra em `atualizar`: mover é operação de funil, com cálculo de
 *  ordem e a recusa da etapa de ganho — ver FunilServico. */
@Injectable({ providedIn: 'root' })
export class ContatosServico {
  private http = inject(HttpClient);
  private pipelines = inject(PipelinesServico);
  private readonly base = `${API}/contatos`;

  listar(
    filtro: FiltroContato, busca?: string, etapaId?: number | null,
    responsavelId?: number | null, pagina = 1, tamanho = 30
  ): Observable<Pagina<ContatoResumo>> {
    let p = new HttpParams().set('filtro', filtro).set('pagina', pagina).set('tamanho', tamanho);
    if (busca) p = p.set('busca', busca);
    if (etapaId != null) p = p.set('etapaId', etapaId);
    if (responsavelId != null) p = p.set('responsavelId', responsavelId);
    return this.http.get<Pagina<ContatoResumo>>(this.base, { params: p });
  }

  detalhe(id: number): Observable<ContatoDetalhe> {
    return this.http.get<ContatoDetalhe>(`${this.base}/${id}`);
  }

  /** ⚠️ `recontarMenu` AQUI E NAS DEMAIS QUE MEXEM NO QUADRO. O contador ao lado de cada funil
   *  no menu é "negócios no quadro", e a lista é carregada uma vez no boot — sem isto ele fica
   *  velho até a pessoa recarregar a página. Ver `PipelinesServico.recontar`. */
  criar(corpo: CorpoContato): Observable<{ id: number }> {
    return this.http.post<{ id: number }>(this.base, corpo)
      .pipe(recontarMenu(this.pipelines));
  }

  atualizar(id: number, corpo: CorpoContato): Observable<void> {
    return this.http.put<void>(`${this.base}/${id}`, corpo);
  }

  /** A PORTA ÚNICA DO GANHO. Arrastar o card para a coluna de venda e clicar em "venda
   *  fechada" chamam este mesmo método — o `mover` do funil recusa a etapa de ganho de
   *  propósito, para não existir um segundo caminho que grava diferente. */
  marcarGanho(id: number, valor: number, canalId: number | null = null): Observable<void> {
    // O total do funil só muda quando a empresa conclui na hora (`dias = 0`), mas recontar
    // sempre é mais barato que acertar quando recontar.
    return this.http.post<void>(`${this.base}/${id}/ganho`, { valor, canalId })
      .pipe(recontarMenu(this.pipelines));
  }

  /** NEG-3 · as campanhas oferecidas no modal de fechamento e a que o sistema detectou.
   *
   *  Chamada ao ABRIR o modal, e não junto do detalhe do contato: o funil abre o mesmo modal a
   *  partir de um card, que não carrega o detalhe. Uma requisição pequena num clique deliberado
   *  custa menos que um campo a mais em cada card do quadro. */
  canaisDoFechamento(id: number): Observable<CanaisDoFechamento> {
    return this.http.get<CanaisDoFechamento>(`${this.base}/${id}/canais-fechamento`);
  }

  marcarPerdido(id: number, motivo: string): Observable<void> {
    return this.http.post<void>(`${this.base}/${id}/perda`, { motivo })
      .pipe(recontarMenu(this.pipelines));
  }

  /** Começa um negócio com este contato (E6).
   *
   *  ⚠️ ERA `reabrir`, e o nome antigo contava metade da história: "reabrir" descreve quem já teve
   *  negócio, e o lead que acabou de chegar pela caixa nunca teve nenhum — que é o caso comum
   *  desde o E6. O gesto é um só; o servidor decide se revive a perda ou abre linha nova.
   *
   *  `pipelineId` ausente deixa o servidor escolher: revive a perda, ou usa o funil do último
   *  negócio ganho, ou o padrão. */
  abrirNegociacao(id: number, pipelineId?: number | null): Observable<void> {
    return this.http.post<void>(`${this.base}/${id}/negociacao`,
      pipelineId == null ? {} : { pipelineId }).pipe(recontarMenu(this.pipelines));
  }

  /** IRREVERSÍVEL. Só dono e gestor (a API devolve 403 para vendedor). */
  anonimizar(id: number): Observable<void> {
    // Anonimizar tira o contato do quadro (`RegrasNegociacao.NoQuadro`), então mexe no contador.
    return this.http.post<void>(`${this.base}/${id}/anonimizar`, {})
      .pipe(recontarMenu(this.pipelines));
  }
}
