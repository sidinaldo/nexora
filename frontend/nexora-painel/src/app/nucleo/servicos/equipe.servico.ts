import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API } from '../api-base';
import { ConviteInfo, LoginResponse, PapelUsuario, StatusUsuario, TokenGerado, UsuarioEquipe } from '../modelos';

@Injectable({ providedIn: 'root' })
export class EquipeServico {
  private http = inject(HttpClient);

  listar(): Observable<UsuarioEquipe[]> {
    return this.http.get<UsuarioEquipe[]>(`${API}/equipe`);
  }

  /** Devolve o TOKEN — não há envio de e-mail na fase 1, o dono copia o link e manda por
   *  fora. Limitação registrada desde o bloco 1. */
  convidar(nome: string, email: string, papel: PapelUsuario): Observable<TokenGerado> {
    return this.http.post<TokenGerado>(`${API}/equipe/convites`, { nome, email, papel });
  }

  reenviarConvite(id: number): Observable<TokenGerado> {
    return this.http.post<TokenGerado>(`${API}/equipe/${id}/reenviar-convite`, {});
  }

  gerarResetSenha(id: number): Observable<TokenGerado> {
    return this.http.post<TokenGerado>(`${API}/equipe/${id}/reset-senha`, {});
  }

  atualizar(id: number, nome: string, papel: PapelUsuario, status: StatusUsuario): Observable<void> {
    return this.http.put<void>(`${API}/equipe/${id}`, { nome, papel, status });
  }

  trocarMinhaSenha(senhaAtual: string, senhaNova: string): Observable<void> {
    return this.http.post<void>(`${API}/conta/senha`, { senhaAtual, senhaNova });
  }

  // ---- fluxos PÚBLICOS (sem sessão) ----

  /** "Esqueci minha senha". Responde 200 com a MESMA mensagem exista o e-mail ou não — nunca
   *  404. Resposta diferente transformaria o endpoint num verificador de contas. */
  solicitarReset(email: string): Observable<{ mensagem: string }> {
    return this.http.post<{ mensagem: string }>(`${API}/redefinir/solicitar`, { email });
  }

  conviteInfo(token: string): Observable<ConviteInfo> {
    return this.http.get<ConviteInfo>(`${API}/convite/${token}`);
  }

  aceitarConvite(token: string, senha: string): Observable<LoginResponse> {
    return this.http.post<LoginResponse>(`${API}/convite/${token}`, { senha });
  }

  resetInfo(token: string): Observable<ConviteInfo> {
    return this.http.get<ConviteInfo>(`${API}/redefinir/${token}`);
  }

  redefinirSenha(token: string, senha: string): Observable<{ ok: boolean }> {
    return this.http.post<{ ok: boolean }>(`${API}/redefinir/${token}`, { senha });
  }
}
