import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API } from '../api-base';
import { ContatoCard, PaginaCursor, QuadroFunil } from '../modelos';

/** O quadro kanban. */
@Injectable({ providedIn: 'root' })
export class FunilServico {
  private http = inject(HttpClient);
  private readonly base = `${API}/funil`;

  /** SEMPRE paginado por coluna: 3.000 leads em "Novo Lead" derrubariam a tela. */
  quadro(porColuna = 50): Observable<QuadroFunil> {
    return this.http.get<QuadroFunil>(this.base, {
      params: new HttpParams().set('porColuna', porColuna)
    });
  }

  /** Mais cards de UMA coluna. O cursor é o par (ordemKanban, id) do último card carregado —
   *  a mesma ordenação do índice, e por valor, não por offset: esta é a tela onde o vendedor
   *  arrasta cards, então entre duas páginas a coluna pode ter sido reordenada. */
  coluna(
    etapaId: number, cursorOrdem: number | null, cursorId: number | null, tamanho = 50
  ): Observable<PaginaCursor<ContatoCard>> {
    let p = new HttpParams().set('tamanho', tamanho);
    if (cursorOrdem != null) p = p.set('cursorOrdem', cursorOrdem);
    if (cursorId != null) p = p.set('cursorId', cursorId);
    return this.http.get<PaginaCursor<ContatoCard>>(
      `${this.base}/etapas/${etapaId}/contatos`, { params: p });
  }

  /** Move ou reordena. `aposContatoId` = o card ACIMA do ponto onde soltou (null = topo).
   *
   *  RECUSA a etapa de ganho com 409 — ao soltar ali, a tela abre o modal de venda em vez de
   *  chamar isto. Devolve a nova ordem para o cliente conferir contra o que pintou de forma
   *  otimista: se divergir, houve renormalização da coluna e ele recarrega. */
  /** `versao` é o `xmin` que veio no card. Se outra pessoa mexeu nele entre a leitura e o
   *  arrasto, a API devolve 409 e a tela recarrega a coluna — em vez de o último a soltar
   *  vencer em silêncio. */
  mover(contatoId: number, etapaId: number, aposContatoId: number | null, versao?: number)
    : Observable<{ ordemKanban: number }> {
    return this.http.post<{ ordemKanban: number }>(
      `${this.base}/${contatoId}/mover`, { etapaId, aposContatoId, versao });
  }
}
