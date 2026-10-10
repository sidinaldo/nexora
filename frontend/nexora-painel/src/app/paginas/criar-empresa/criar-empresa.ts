import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { CadastroServico } from '../../nucleo/servicos/cadastro.servico';

/** Criar uma empresa CLIENTE — tela PÚBLICA, do OPERADOR, não do cliente.
 *
 *  ===================== ELA SUBSTITUI UM SSH =====================
 *  Até aqui, criar cliente era entrar na VPS e rodar `deploy/criar-empresa.sh`. O script CONTINUA
 *  existindo, e por um motivo concreto: ele fala com a API por dentro da rede do Docker, então
 *  funciona com o túnel fora do ar — que é exatamente quando se quer controle local. Esta tela é o
 *  caminho normal; ele é o caminho que sobrevive.
 *
 *  ===================== A CREDENCIAL NÃO É SESSÃO =====================
 *  Não há sessão aqui: a empresa ainda não existe e o operador não tem conta nela. A credencial é
 *  a chave de administração, no cabeçalho `X-Chave-Admin`, por requisição. Ela NÃO vai para o
 *  localStorage, não vai para a URL, não é guardada por serviço nenhum, e some do formulário no
 *  sucesso.
 *
 *  ⚠️ CHAVE ERRADA RESPONDE 401 — e 401, para o resto do painel, significa "sessão venceu". Ver a
 *  linha de `/api/cadastro/` em `interceptor-token.ts`: sem ela, um caractere errado na chave apaga
 *  a sessão do operador e leva embora o formulário inteiro.
 *
 *  ===================== O ENVIO NÃO É IDEMPOTENTE =====================
 *  Repetir depois de uma falha ambígua criaria uma segunda empresa — se não fosse a unicidade
 *  GLOBAL do e-mail, que transforma a segunda tentativa num 409 limpo. É uma rede de proteção
 *  acidental, e é por causa dela que a mensagem de rede caída pode dizer "confira" em vez de
 *  "não repita".
 *  ===================================================================== */
@Component({
  selector: 'app-criar-empresa',
  imports: [FormsModule, RouterLink],
  templateUrl: './criar-empresa.html',
  styleUrl: './criar-empresa.css'
})
export class CriarEmpresa {
  private servico = inject(CadastroServico);

  chave = signal('');
  nome = signal('');
  documento = signal('');
  nomeDono = signal('');
  emailDono = signal('');
  senha = signal('');
  senha2 = signal('');

  enviando = signal(false);
  erro = signal('');
  /** O desfecho ambíguo: a requisição não voltou, e a empresa pode ou não existir. Separado do
   *  `erro` porque pede uma ação diferente — conferir antes de repetir, não corrigir e reenviar. */
  incerto = signal('');
  criada = signal<{ empresaId: number; nome: string; emailDono: string } | null>(null);

  podeEnviar = computed(() =>
    !this.enviando()
    && this.chave().trim().length > 0
    && this.nome().trim().length > 0
    && this.nomeDono().trim().length > 0
    && this.emailDono().trim().length > 0
    && this.senha().length > 0);

  criar() {
    // As mesmas duas frases do `redefinir.ts:40-41`, palavra por palavra. A primeira é idêntica à
    // que o servidor produz (`ServicoCadastroEmpresa.cs:37`), então o operador lê a mesma coisa
    // independentemente de qual lado pegou o erro.
    if (this.senha().length < 8) { this.erro.set('A senha precisa de ao menos 8 caracteres.'); return; }
    if (this.senha() !== this.senha2()) { this.erro.set('As duas senhas estão diferentes. Digite a mesma nos dois campos.'); return; }
    if (!this.podeEnviar()) return;

    const nome = this.nome().trim();
    const emailDono = this.emailDono().trim();
    const documento = this.documento().trim();

    this.enviando.set(true);
    this.erro.set('');
    this.incerto.set('');

    this.servico.criarEmpresa(this.chave().trim(), {
      nome,
      documento: documento.length > 0 ? documento : null,
      nomeDono: this.nomeDono().trim(),
      emailDono,
      senha: this.senha()
    }).subscribe({
      next: r => {
        // Guarda o que a tela de sucesso vai mostrar ANTES de limpar: o formulário é zerado, e
        // sem isto o resumo apareceria vazio.
        this.criada.set({ empresaId: r.empresaId, nome, emailDono });
        this.limpar();
        this.enviando.set(false);
      },
      error: e => {
        this.enviando.set(false);
        // ⚠️ NÃO LIMPA NADA. Chave errada ou e-mail repetido não podem custar os dados do cliente
        // já digitados — é o ponto inteiro da linha de `/api/cadastro/` no interceptor.
        const doServidor: string | undefined = e.error?.erro;

        if (e.status === 0) {
          this.incerto.set(
            'Não foi possível falar com o servidor. A empresa pode ter sido criada mesmo assim — ' +
            'confira antes de tentar de novo.');
          return;
        }

        if (e.status === 401) {
          // Texto próprio, e nunca o "Não autorizado." do servidor: ele é a mesma frase para três
          // situações diferentes e não dá o que fazer. Aqui nomeamos as três SEM afirmar qual é —
          // o servidor se recusa a distinguir chave ausente de chave errada, e a tela faz igual.
          this.erro.set(
            'A chave de administração não foi aceita. Confira se colou a chave inteira, sem ' +
            'espaços antes ou depois. Se ela estiver certa, é porque o cadastro está DESLIGADO no ' +
            'servidor: a chave precisa estar preenchida no .env e a API precisa ter sido recriada ' +
            'depois disso. Nada foi criado.');
          return;
        }

        if (e.status >= 500) {
          this.incerto.set('Erro inesperado no servidor. Nada confirmado — confira antes de repetir.');
          return;
        }

        // 409 e 429 já chegam acionáveis do servidor; 400 também, quando vem do serviço. Quando o
        // 400 vem do model binding é `ValidationProblemDetails` (em inglês, moldado pelo
        // framework) e `erro` não existe — daí o texto genérico em vez de renderizar aquilo.
        this.erro.set(doServidor ?? 'Não foi possível criar: confira os campos e tente de novo.');
      }
    });
  }

  /** Volta ao formulário vazio. Zera a chave também: a tela de sucesso é terminal, e manter a
   *  chave pouparia uma colagem ao custo de um segredo vivo num signal e no DOM enquanto a aba
   *  ficar aberta — que pode ser a tarde inteira. Uma colagem por empresa é o preço certo. */
  outra() {
    this.criada.set(null);
    this.erro.set('');
    this.incerto.set('');
    this.limpar();
  }

  private limpar() {
    this.chave.set('');
    this.nome.set('');
    this.documento.set('');
    this.nomeDono.set('');
    this.emailDono.set('');
    this.senha.set('');
    this.senha2.set('');
  }
}
