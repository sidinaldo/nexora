import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Router } from '@angular/router';
import { Observable, tap } from 'rxjs';
import { API } from '../api-base';
import { LoginResponse, Permissao, UsuarioAutenticado } from '../modelos';

export const CHAVE_TOKEN = 'nexora.token';
export const CHAVE_USUARIO = 'nexora.usuario';

@Injectable({ providedIn: 'root' })
export class AuthServico {
  private http = inject(HttpClient);
  private router = inject(Router);

  readonly usuario = signal<UsuarioAutenticado | null>(this.usuarioSalvo());
  readonly autenticado = computed(() => this.usuario() !== null);

  /** ===================== A TELA PERGUNTA O GESTO, NÃO O PAPEL =====================
   *  Aqui havia `ehDono`, `ehGestor` e `podeGerenciar`, e umas vinte e cinco telas decidiam com
   *  eles o que oferecer — uma segunda cópia da regra que o servidor aplica nas rotas. As duas já
   *  discordaram: a importação aceitava o gestor no servidor, e a tela, escrita com `ehDono`,
   *  escondia dele o botão.
   *
   *  Agora quem pode o quê vem PRONTO do servidor (`permissoes`, no login), da mesma tabela que
   *  trava as rotas. A tela só pergunta: "posso importar?". Sem a lista, a resposta é NÃO — o
   *  papel nunca é usado para adivinhar.
   *  ================================================================================ */
  pode(permissao: Permissao): boolean {
    return this.usuario()?.permissoes?.includes(permissao) ?? false;
  }

  /** `false` para a sessão aberta antes de a lista existir: ela guardou o usuário sem
   *  `permissoes`, e até o servidor responder não há o que consultar. */
  readonly permissoesConhecidas = computed(() => Array.isArray(this.usuario()?.permissoes));

  /** Pergunta ao servidor o que a sessão atual pode — do papel do TOKEN, o mesmo que as rotas
   *  conferem. O shell chama ao abrir: cobre a sessão antiga e a tabela que mudou num deploy.
   *
   *  ⚠️ Resposta que não é lista NÃO apaga o que já se sabia: trocar as permissões de alguém por
   *  lixo esconderia a tela inteira dele por causa de uma resposta malformada. */
  atualizarPermissoes(): Observable<Permissao[]> {
    return this.http.get<Permissao[]>(`${API}/auth/permissoes`).pipe(
      tap(permissoes => {
        const atual = this.usuario();
        if (!atual || !Array.isArray(permissoes)) return;
        const novo = { ...atual, permissoes };
        localStorage.setItem(CHAVE_USUARIO, JSON.stringify(novo));
        this.usuario.set(novo);
      })
    );
  }

  entrar(email: string, senha: string): Observable<LoginResponse> {
    return this.http.post<LoginResponse>(`${API}/auth/login`, { email, senha }).pipe(
      tap(r => this.aplicarLogin(r))
    );
  }

  /** Usado também pelo aceite de convite, que já devolve token + usuário como o login. */
  aplicarLogin(r: LoginResponse): void {
    localStorage.setItem(CHAVE_TOKEN, r.token);
    localStorage.setItem(CHAVE_USUARIO, JSON.stringify(r.usuario));
    this.usuario.set(r.usuario);
  }

  /** Atualiza só o nome exibido, depois que a pessoa edita a própria conta.
   *
   *  O nome vem do JWT, e o JWT NÃO é reemitido nessa edição — reemitir token a cada troca de
   *  nome trocaria a sessão por um detalhe de cadastro. Sem esta atualização local, a barra
   *  lateral mostraria o nome antigo até o próximo login, e o usuário acharia que não salvou. */
  atualizarNome(nome: string): void {
    const atual = this.usuario();
    if (!atual) return;
    const novo = { ...atual, nome };
    localStorage.setItem(CHAVE_USUARIO, JSON.stringify(novo));
    this.usuario.set(novo);
  }

  sair(): void {
    this.limpar();
    this.router.navigate(['/entrar']);
  }

  /** Chamado pelo interceptor quando a API devolve 401 (token expirado). */
  limpar(): void {
    localStorage.removeItem(CHAVE_TOKEN);
    localStorage.removeItem(CHAVE_USUARIO);
    this.usuario.set(null);
  }

  get token(): string | null {
    return localStorage.getItem(CHAVE_TOKEN);
  }

  private usuarioSalvo(): UsuarioAutenticado | null {
    const bruto = localStorage.getItem(CHAVE_USUARIO);
    if (!bruto) return null;
    try { return JSON.parse(bruto); } catch { return null; }
  }
}
