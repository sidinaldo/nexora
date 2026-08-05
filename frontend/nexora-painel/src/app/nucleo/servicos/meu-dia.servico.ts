import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API } from '../api-base';
import { LembreteDto, MeuDia } from '../modelos';

/** O plano do dia e os lembretes manuais.
 *
 *  O Meu Dia NÃO tem tabela: é derivado de conversas esperando resposta + lembretes vencidos.
 *  Responder ou concluir remove a linha sozinho, sem nenhuma sincronização. */
@Injectable({ providedIn: 'root' })
export class MeuDiaServico {
  private http = inject(HttpClient);

  meuDia(): Observable<MeuDia> {
    return this.http.get<MeuDia>(`${API}/meu-dia`);
  }

  doContato(contatoId: number): Observable<LembreteDto[]> {
    return this.http.get<LembreteDto[]>(`${API}/lembretes/contato/${contatoId}`);
  }

  criar(corpo: {
    contatoId: number; dataAlvo: string; horaAlvo?: string | null;
    titulo: string; observacao?: string | null;
  }): Observable<{ id: number }> {
    return this.http.post<{ id: number }>(`${API}/lembretes`, corpo);
  }

  concluir(id: number): Observable<void> {
    return this.http.post<void>(`${API}/lembretes/${id}/concluir`, {});
  }

  cancelar(id: number): Observable<void> {
    return this.http.post<void>(`${API}/lembretes/${id}/cancelar`, {});
  }
}
