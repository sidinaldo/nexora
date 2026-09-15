import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API } from '../api-base';
import { PipelinesServico, recontarMenu } from './pipelines.servico';
import { CardFunil, PaginaCursor, QuadroFunil } from '../modelos';

/** O quadro kanban. */
@Injectable({ providedIn: 'root' })
export class FunilServico {
  private http = inject(HttpClient);
  private pipelines = inject(PipelinesServico);
  private readonly base = `${API}/funil`;

  /** SEMPRE paginado por coluna: 3.000 leads em "Novo Lead" derrubariam a tela.
   *
   *  `pipeline` nulo cai na padrão do servidor — é o que dá destino a quem abre `/crm` sem
   *  escolher funil, e o que mantém um link antigo funcionando. */
  quadro(pipeline: number | null = null, porColuna = 50): Observable<QuadroFunil> {
    let p = new HttpParams().set('porColuna', porColuna);
    if (pipeline != null) p = p.set('pipeline', pipeline);
    return this.http.get<QuadroFunil>(this.base, { params: p });
  }

  /** Mais cards de UMA coluna. O cursor é o par (ordemKanban, id) do último card carregado —
   *  a mesma ordenação do índice, e por valor, não por offset: esta é a tela onde o vendedor
   *  arrasta cards, então entre duas páginas a coluna pode ter sido reordenada. */
  coluna(
    etapaId: number, cursorOrdem: number | null, cursorId: number | null, tamanho = 50
  ): Observable<PaginaCursor<CardFunil>> {
    let p = new HttpParams().set('tamanho', tamanho);
    if (cursorOrdem != null) p = p.set('cursorOrdem', cursorOrdem);
    if (cursorId != null) p = p.set('cursorId', cursorId);
    return this.http.get<PaginaCursor<CardFunil>>(
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
  /** ⚠️ `negociacaoId`, não `contatoId` — desde o E4c/2 é a negociação que se move, e um
   *  contato pode ter duas no quadro. Os dois são `number`: trocar um pelo outro compila. */
  mover(negociacaoId: number, etapaId: number, aposNegociacaoId: number | null, versao?: number)
    : Observable<{ ordemKanban: number }> {
    // Arrastar ENTRE funis tira de um contador e põe noutro — os dois ficariam velhos.
    return this.http.post<{ ordemKanban: number }>(
      `${this.base}/${negociacaoId}/mover`, { etapaId, aposNegociacaoId, versao })
      .pipe(recontarMenu(this.pipelines));
  }
}
