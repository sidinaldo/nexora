import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API } from '../api-base';
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

  criar(corpo: CorpoContato): Observable<{ id: number }> {
    return this.http.post<{ id: number }>(this.base, corpo);
  }

  atualizar(id: number, corpo: CorpoContato): Observable<void> {
    return this.http.put<void>(`${this.base}/${id}`, corpo);
  }

  /** A PORTA ÚNICA DO GANHO. Arrastar o card para a coluna de venda e clicar em "venda
   *  fechada" chamam este mesmo método — o `mover` do funil recusa a etapa de ganho de
   *  propósito, para não existir um segundo caminho que grava diferente. */
  marcarGanho(id: number, valor: number): Observable<void> {
    return this.http.post<void>(`${this.base}/${id}/ganho`, { valor });
  }

  marcarPerdido(id: number, motivo: string): Observable<void> {
    return this.http.post<void>(`${this.base}/${id}/perda`, { motivo });
  }

  reabrir(id: number): Observable<void> {
    return this.http.post<void>(`${this.base}/${id}/reabrir`, {});
  }

  /** IRREVERSÍVEL. Só dono e gestor (a API devolve 403 para vendedor). */
  anonimizar(id: number): Observable<void> {
    return this.http.post<void>(`${this.base}/${id}/anonimizar`, {});
  }
}
