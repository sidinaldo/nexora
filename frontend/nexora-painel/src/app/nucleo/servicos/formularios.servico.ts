import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API } from '../api-base';
import { FormularioDto, PaginaComTotal } from '../modelos';

/** Formulários de captação do site.
 *
 *  Só quem pode `gerenciar_captacao` usa — a API devolve 403 para os outros. A rota tem a
 *  guarda dessa permissão, mas quem decide é o servidor. */
@Injectable({ providedIn: 'root' })
export class FormulariosServico {
  private http = inject(HttpClient);

  /** Uma página, com o total e as páginas prontos do servidor (AUD-XX, #21). */
  listar(pagina: number, tamanho: number): Observable<PaginaComTotal<FormularioDto>> {
    return this.http.get<PaginaComTotal<FormularioDto>>(
      `${API}/formularios`, { params: { pagina, tamanho } });
  }

  criar(nome: string, dominioPermitido: string | null): Observable<{ id: number }> {
    return this.http.post<{ id: number }>(`${API}/formularios`, { nome, dominioPermitido });
  }

  atualizar(id: number, nome: string, dominioPermitido: string | null): Observable<void> {
    return this.http.put<void>(`${API}/formularios/${id}`, { nome, dominioPermitido });
  }

  alternarAtivo(id: number, ativo: boolean): Observable<void> {
    return this.http.post<void>(`${API}/formularios/${id}/ativo`, { ativo });
  }

  /** Regera a chave. A antiga para de funcionar NA HORA — o HTML publicado no site precisa ser
   *  trocado, e a tela avisa isso antes de confirmar. */
  regerarChave(id: number): Observable<{ chave: string }> {
    return this.http.post<{ chave: string }>(`${API}/formularios/${id}/chave`, {});
  }

  /** O endereço que o formulário do site chama.
   *
   *  Sai do `API` do environment — NUNCA escrito à mão. O snippet gerado vai para o site do
   *  cliente e fica lá por anos; uma URL chumbada aqui quebraria em produção sem aviso. */
  urlPublica(chave: string): string {
    return `${API}/captura/${chave}`;
  }
}
